using ClaudeUsage.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeUsage.Services;

// This lane performs only local reads. Export/auth/network failures cannot affect Claude/Codex.
public sealed class OpenRouterSpendPoller : BackgroundService
{
    private const int IntervalSec = 180;

    private readonly OpenRouterSpendState _state;
    private readonly IHubContext<UsageHub> _hub;
    private readonly ILogger<OpenRouterSpendPoller> _logger;

    public OpenRouterSpendPoller(OpenRouterSpendState state, IHubContext<UsageHub> hub,
        ILogger<OpenRouterSpendPoller> logger)
    {
        _state = state;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = OpenRouterSpendReader.Read();
                _state.Apply(result, DateTimeOffset.UtcNow);
                if (result.Detail is { } detail)
                    _logger.LogWarning("OpenRouter account spend: {Detail}", detail);
            }
            catch
            {
                _state.Apply(new(OpenRouterSpendReader.ReadStatus.Unavailable), DateTimeOffset.UtcNow);
                _logger.LogWarning("OpenRouter account spend poll failed; other usage lanes unaffected.");
            }

            // Publish the wrapper even without data so missing/unavailable sources do not look
            // like zero spending. A client delivery fault is not an upstream freshness failure.
            try
            {
                await _hub.Clients.All.SendAsync("openRouterSpendUpdated", _state.Current, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch
            {
                _logger.LogWarning("OpenRouter account spend update could not be delivered.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(IntervalSec), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
