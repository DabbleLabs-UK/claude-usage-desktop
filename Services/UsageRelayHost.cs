using ClaudeUsage.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeUsage.Services;

// Headless, LAN-only companion mode. It deliberately exposes one authenticated endpoint and
// calculates usage on the host that owns the Claude login. It never serializes credentials,
// refresh tokens, prompts, responses, or command history.
public static class UsageRelayHost
{
    public static async Task<int> RunAsync(int port, string accessToken, CancellationToken ct = default)
    {
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(accessToken))
        {
            Console.Error.WriteLine("Relay requires a valid --relay-port and non-empty --relay-token.");
            return 2;
        }

        var settings = new SettingsService();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<AuthRefreshLog>();
        builder.Services.AddSingleton<PollLog>();
        builder.Services.AddSingleton<ClaudeCli>();
        builder.Services.AddSingleton<UsageService>();

        var app = builder.Build();
        app.MapGet("/v1/usage", async (HttpRequest request, UsageService usage) =>
        {
            if (!UsageRelayAuthorization.IsAuthorized(request.Headers["X-Claude-Usage-Relay-Token"].ToString(), accessToken))
                return Results.Unauthorized();

            try
            {
                var data = await usage.FetchAsync();
                return Results.Ok(data with { IsStale = false });
            }
            catch (Exception)
            {
                // Never return internal exception details: they can reveal local paths or auth state.
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });

        await app.StartAsync(ct);
        Console.WriteLine($"Claude Usage relay listening on port {port}. Press Ctrl+C to stop.");
        try { await app.WaitForShutdownAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        await app.StopAsync(TimeSpan.FromSeconds(3));
        return 0;
    }
}
