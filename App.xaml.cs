using System.Text.Json;
using System.Windows;
using ClaudeUsage.Hubs;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ClaudeUsage;

public partial class App : Application
{
    private IHost? _host;
    private WinForms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;

    public bool IsQuitting { get; private set; }
    public SettingsService SettingsService { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WinForms.Application.EnableVisualStyles();

        SettingsService = new SettingsService();
        _host = BuildWebApp();
        await _host.StartAsync();

        InitTrayIcon();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _mainWindow = new MainWindow();
        _mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _notifyIcon?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        base.OnExit(e);
    }

    public void Quit()
    {
        IsQuitting = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        Shutdown();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null || !_mainWindow.IsLoaded)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Show();
        }
        else
        {
            if (!_mainWindow.IsVisible) _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
    }

    public void ShowSettingsView()
    {
        ShowMainWindow();
        _mainWindow?.OpenSettings();
    }

    private void InitTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Settings", null, (_, _) => ShowSettingsView());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = true,
            Text = "Claude Usage",
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private static Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(16, 16, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(Drawing.Color.Transparent);
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(167, 139, 250));
            g.FillEllipse(fill, 1, 1, 13, 13);
            using var font = new Drawing.Font("Segoe UI", 7.5f, Drawing.FontStyle.Bold);
            using var textBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(13, 13, 26));
            g.DrawString("C", font, textBrush, 3f, 2f);
        }
        // GetHicon creates an independent HICON; handle lives for app lifetime
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _host?.Services.GetRequiredService<UsagePoller>().TriggerImmediatePoll();
    }

    private IHost BuildWebApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        builder.WebHost.UseSetting("urls", $"http://0.0.0.0:{SettingsService.Current.ServerPort}");

        builder.Logging.ClearProviders();
#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddCors(options =>
            options.AddDefaultPolicy(policy =>
                policy.SetIsOriginAllowed(_ => true)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials()));

        builder.Services.AddSignalR()
            .AddJsonProtocol(o =>
                o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        builder.Services.AddSingleton(SettingsService);
        builder.Services.AddSingleton<UsageState>();
        builder.Services.AddSingleton<UsageService>();
        builder.Services.AddSingleton<UsagePoller>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UsagePoller>());

        var app = builder.Build();

        app.UseCors();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapHub<UsageHub>("/hub/usage");

        app.MapGet("/api/usage", (UsageState state) =>
            state.Current is { } data
                ? Results.Ok(data)
                : Results.NotFound(new { message = "No data yet." }));

        app.MapGet("/api/settings", (SettingsService settings) =>
            Results.Ok(settings.Current with { StartWithWindows = settings.GetActualAutostart() }));

        app.MapPost("/api/settings", (AppSettings body, SettingsService settings) =>
        {
            settings.Save(body);
            return Results.Ok(settings.Current with { StartWithWindows = settings.GetActualAutostart() });
        });

        return app;
    }
}
