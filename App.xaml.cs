using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    private static readonly string InstanceId = Guid.NewGuid().ToString("N");

    private static Mutex? _singleInstanceMutex;
    private IHost? _host;
    private WinForms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private bool _trayIconAvailable;

    public bool IsQuitting { get; private set; }
    public bool TrayIconAvailable => _trayIconAvailable;
    public SettingsService SettingsService { get; private set; } = null!;
    private FirewallService _firewallService = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Single-instance guard: only one poller should ever hit the endpoint at a time.
        _singleInstanceMutex = new Mutex(true, "Local\\ClaudeUsage_SingleInstance_v1", out bool createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Current.Shutdown();
            return;
        }

        base.OnStartup(e);
        WinForms.Application.EnableVisualStyles();

        SettingsService = new SettingsService();
        _firewallService = new FirewallService();
        _host = BuildWebApp();
        await _host.StartAsync();

        InitTrayIcon();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _mainWindow = new MainWindow();
        _mainWindow.Show();

        _ = Task.Run(CheckFirewallOnStartupAsync);
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
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
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
        try
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
            _trayIconAvailable = true;
        }
        catch
        {
            // If tray icon init fails, _trayIconAvailable stays false.
            // MainWindow.OnClosing will not hide-to-tray in that case,
            // so the user is never left with a running but unreachable app.
            _trayIconAvailable = false;
        }
    }

    private static Drawing.Icon CreateTrayIcon()
    {
        // Primary: read the Win32 PE icon resources from the running exe — the same
        // icon that the taskbar and window title-bar show (set by <ApplicationIcon>).
        // This works in self-contained single-file publish because ProcessPath points
        // to the bundled exe, which carries the PE icon resources in its header.
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath is not null)
            {
                var icon = Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null) return icon;
            }
        }
        catch { }

        // Fallback: load from the embedded .NET resource — search by suffix so the
        // exact managed resource name doesn't need to be hardcoded.
        try
        {
            var asm = typeof(App).Assembly;
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("logo_icon.ico", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream is not null)
                    return new Drawing.Icon(stream);
            }
        }
        catch { }

        return SystemIcons.Application;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _host?.Services.GetRequiredService<UsagePoller>().TriggerImmediatePoll();
    }

    private async Task CheckFirewallOnStartupAsync()
    {
        var settings = SettingsService.Current;
        var port = settings.ServerPort;

        if (_firewallService.RuleExists(port))
        {
            if (!settings.FirewallSetupDone)
                SettingsService.Save(settings with { FirewallSetupDone = true });
            return;
        }

        if (settings.FirewallDeclined)
            return;

        var result = await _firewallService.TryAddRuleAsync(port);
        switch (result)
        {
            case FirewallResult.Added:
                SettingsService.Save(SettingsService.Current with { FirewallSetupDone = true, FirewallDeclined = false });
                break;
            case FirewallResult.Declined:
                SettingsService.Save(SettingsService.Current with { FirewallDeclined = true });
                break;
        }
    }

    private static readonly string[] VirtualAdapterKeywords =
    [
        "VirtualBox", "VMware", "Hyper-V", "vEthernet", "WSL",
        "Bluetooth", "Loopback", "TAP", "Virtual"
    ];

    private static bool IsVirtualAdapter(NetworkInterface ni) =>
        VirtualAdapterKeywords.Any(kw =>
            ni.Description.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
            ni.Name.Contains(kw, StringComparison.OrdinalIgnoreCase));

    private static bool HasIpv4Gateway(NetworkInterface ni) =>
        ni.GetIPProperties().GatewayAddresses
            .Any(gw => gw.Address.AddressFamily == AddressFamily.InterNetwork);

    private static string? GetPrivateIp(NetworkInterface ni) =>
        ni.GetIPProperties().UnicastAddresses
            .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork
                      && !ua.Address.ToString().StartsWith("169.254"))
            .Select(ua => ua.Address.ToString())
            .FirstOrDefault(addr => addr.StartsWith("192.168.")
                                 || addr.StartsWith("10.")
                                 || IsPrivate172(addr));

    private static IReadOnlyList<(string Name, string Ip, bool IsVirtual)> GetAdapterCandidates()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                      && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                      && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Select(ni => (ni, ip: GetPrivateIp(ni)))
            .Where(t => t.ip is not null)
            .OrderByDescending(t => !IsVirtualAdapter(t.ni))
            .ThenByDescending(t => HasIpv4Gateway(t.ni))
            .ThenByDescending(t => t.ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                                || t.ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .Select(t => (t.ni.Name, t.ip!, IsVirtual: IsVirtualAdapter(t.ni)))
            .ToList();
    }

    private static string? GetLanIp(string? preferredAdapterName = null)
    {
        var candidates = GetAdapterCandidates();

        // If a preferred adapter is stored, try to find it first
        if (preferredAdapterName is not null)
        {
            var preferred = candidates.FirstOrDefault(c =>
                string.Equals(c.Name, preferredAdapterName, StringComparison.OrdinalIgnoreCase));
            if (preferred.Ip is not null) return preferred.Ip;
        }

        // Auto: prefer physical adapters first
        var autoIp = candidates.Where(c => !c.IsVirtual).Select(c => c.Ip).FirstOrDefault();
        if (autoIp is not null) return autoIp;

        // Fall back to any candidate (virtual included)
        return candidates.Select(c => c.Ip).FirstOrDefault();
    }

    private static bool IsPrivate172(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length == 4
            && int.TryParse(parts[1], out var second)
            && second >= 16 && second <= 31;
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
        builder.Services.AddSingleton(_firewallService);
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

        app.MapGet("/api/backoff", (UsageState state) =>
            Results.Ok(state.Backoff));

        app.MapGet("/api/settings", (SettingsService settings) =>
            Results.Ok(settings.Current with { StartWithWindows = settings.GetActualAutostart() }));

        app.MapPost("/api/settings", (AppSettings body, SettingsService settings) =>
        {
            settings.Save(body);
            return Results.Ok(settings.Current with { StartWithWindows = settings.GetActualAutostart() });
        });

        app.MapGet("/api/network-info", (SettingsService settings) =>
            Results.Ok(new { lanIp = GetLanIp(settings.Current.PreferredAdapterName), port = settings.Current.ServerPort }));

        app.MapGet("/api/adapters", (SettingsService settings) =>
        {
            var preferred = settings.Current.PreferredAdapterName;
            var autoIp = GetLanIp(); // auto-selected (no preference)
            var candidates = GetAdapterCandidates();
            var result = candidates.Select(c => new
            {
                name       = c.Name,
                ip         = c.Ip,
                label      = $"{c.Name} — {c.Ip}{(c.IsVirtual ? " [virtual/VPN]" : "")}",
                isVirtual  = c.IsVirtual,
                isAutoSelected = c.Ip == autoIp && preferred is null,
            });
            return Results.Ok(result);
        });

        app.MapGet("/api/instance", () =>
            Results.Ok(new { instanceId = InstanceId }));

        app.MapGet("/api/version", () =>
        {
            var v = typeof(App).Assembly.GetName().Version;
            return Results.Ok(new { version = v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}" });
        });

        app.MapPost("/api/firewall/enable", async (SettingsService settingsSvc, FirewallService firewallSvc) =>
        {
            var port = settingsSvc.Current.ServerPort;

            if (firewallSvc.RuleExists(port))
            {
                settingsSvc.Save(settingsSvc.Current with { FirewallSetupDone = true, FirewallDeclined = false });
                return Results.Ok(new { status = "already_exists" });
            }

            // Clear declined so the attempt is recorded fresh
            settingsSvc.Save(settingsSvc.Current with { FirewallDeclined = false });

            var result = await firewallSvc.TryAddRuleAsync(port);
            switch (result)
            {
                case FirewallResult.Added:
                    settingsSvc.Save(settingsSvc.Current with { FirewallSetupDone = true, FirewallDeclined = false });
                    return Results.Ok(new { status = "added" });
                case FirewallResult.Declined:
                    settingsSvc.Save(settingsSvc.Current with { FirewallDeclined = true });
                    return Results.Ok(new { status = "declined" });
                default:
                    return Results.Ok(new { status = "error" });
            }
        });

        return app;
    }
}
