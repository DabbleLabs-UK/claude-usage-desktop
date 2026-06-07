using System.Text.Json;
using System.Windows;
using ClaudeUsage.Hubs;
using ClaudeUsage.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeUsage;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = BuildWebApp();
        await _host.StartAsync();
        new MainWindow().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        base.OnExit(e);
    }

    private static IHost BuildWebApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        builder.WebHost.UseSetting("urls", "http://0.0.0.0:5005");

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

        builder.Services.AddSingleton<UsageState>();
        builder.Services.AddSingleton<UsageService>();
        builder.Services.AddHostedService<UsagePoller>();

        var app = builder.Build();

        app.UseCors();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapHub<UsageHub>("/hub/usage");
        app.MapGet("/api/usage", (UsageState state) =>
            state.Current is { } data
                ? Results.Ok(data)
                : Results.NotFound(new { message = "No data yet." }));

        return app;
    }
}
