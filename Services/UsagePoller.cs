using System.Net;
using ClaudeUsage.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeUsage.Services;

public sealed class UsagePoller : BackgroundService
{
    private readonly UsageService _usageService;
    private readonly UsageState _state;
    private readonly IHubContext<UsageHub> _hub;
    private readonly ILogger<UsagePoller> _logger;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    public UsagePoller(
        UsageService usageService,
        UsageState state,
        IHubContext<UsageHub> hub,
        ILogger<UsagePoller> logger)
    {
        _usageService = usageService;
        _state = state;
        _hub = hub;
        _logger = logger;
    }

    public void TriggerImmediatePoll()
    {
        try { _wakeSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAsync(stoppingToken);
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try
            {
                await Task.WhenAny(
                    Task.Delay(TimeSpan.FromSeconds(180), delayCts.Token),
                    _wakeSignal.WaitAsync(stoppingToken));
            }
            finally
            {
                delayCts.Cancel();
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            var data = await _usageService.FetchAsync();
            _state.Update(data);
            await _hub.Clients.All.SendAsync("usageUpdated", data, ct);
            _logger.LogInformation("Usage fetched: 5h={FiveHour}% 7d={SevenDay}%",
                data.FiveHour?.Utilization, data.SevenDay?.Utilization);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("401 -- token expired. Marking stale.");
            _state.MarkStale();
            if (_state.Current is { } stale)
                await _hub.Clients.All.SendAsync("usageUpdated", stale, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("429 -- rate limited. Keeping cache.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching usage.");
        }
    }
}
