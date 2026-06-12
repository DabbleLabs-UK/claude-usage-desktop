using System.Net;
using ClaudeUsage.Hubs;
using ClaudeUsage.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeUsage.Services;

public sealed class UsagePoller : BackgroundService
{
    private const int BaseIntervalSec = 180;
    private const int MaxIntervalSec  = 1800;

    private readonly UsageService _usageService;
    private readonly UsageState _state;
    private readonly UsageLog _log;
    private readonly IHubContext<UsageHub> _hub;
    private readonly ILogger<UsagePoller> _logger;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    private int _failureCount    = 0;
    private int _currentInterval = BaseIntervalSec;

    public UsagePoller(
        UsageService usageService,
        UsageState state,
        UsageLog log,
        IHubContext<UsageHub> hub,
        ILogger<UsagePoller> logger)
    {
        _usageService = usageService;
        _state = state;
        _log = log;
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
        // Seed last-known usage from the persisted log so a relaunch shows the previous values
        // (dimmed, marked stale) immediately -- instead of a blank page -- until the first poll
        // succeeds. If that first poll fails (e.g. overnight auth/backoff), the seed keeps the
        // UI populated; a success overwrites it with fresh, non-stale data.
        if (_state.Current is null)
        {
            var seed = _log.ReadLastSample();
            if (seed is not null)
            {
                _state.Update(seed with { IsStale = true });
                _logger.LogInformation("Seeded last-known usage from persisted log (stale; fetchedAt={At}).",
                    seed.FetchedAt);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAsync(stoppingToken);
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try
            {
                await Task.WhenAny(
                    Task.Delay(TimeSpan.FromSeconds(_currentInterval), delayCts.Token),
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

            // Persist a sample on success only (never on a failed poll). Best-effort:
            // UsageLog swallows its own faults so logging can't disrupt polling.
            _log.Append(data);

            // Clear backoff on success
            if (_failureCount > 0)
            {
                _failureCount    = 0;
                _currentInterval = BaseIntervalSec;
                _state.SetBackoff(null);
                await _hub.Clients.All.SendAsync("backoffUpdated", (BackoffInfo?)null, ct);
            }

            await _hub.Clients.All.SendAsync("usageUpdated", data, ct);
            _logger.LogInformation("Usage fetched: 5h={FiveHour}% 7d={SevenDay}% ({Count} windows)",
                data.Windows.GetValueOrDefault("five_hour")?.Utilization,
                data.Windows.GetValueOrDefault("seven_day")?.Utilization,
                data.Windows.Count);
        }
        catch (RefreshRateLimitedException ex)
        {
            // Own token refresh is suppressed by the sticky 429 cooldown and disk had nothing
            // fresher to adopt. This is NOT an ordinary auth failure: we are deliberately not
            // poking the rate-limited token endpoint and are waiting for the host `claude` to
            // refresh on disk (the primary recovery path). Do NOT ramp the poll backoff -- keep
            // the base cadence so each cycle re-checks disk-adoption and recovers promptly.
            _logger.LogWarning("Token endpoint rate-limited; own refresh suppressed until {Until}. Polling continues at base cadence to await host disk refresh.", ex.SuppressedUntilMs);
            _state.MarkStale();
            await ApplyRefreshRateLimitedAsync(ct);
            if (_state.Current is { } stale)
                await _hub.Clients.All.SendAsync("usageUpdated", stale, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("401 -- auth error. Backing off (failure #{Count}).", _failureCount + 1);
            _state.MarkStale();
            await ApplyBackoffAsync("auth", ct);
            if (_state.Current is { } stale)
                await _hub.Clients.All.SendAsync("usageUpdated", stale, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("429 -- rate limited. Backing off (failure #{Count}).", _failureCount + 1);
            await ApplyBackoffAsync("rate_limit", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching usage. Backing off (failure #{Count}).", _failureCount + 1);
            await ApplyBackoffAsync("other", ct);
        }
    }

    // Increments failure count, doubles interval (capped), updates state, pushes hub message.
    // Schedule: failure 1 → 180s, 2 → 360s, 3 → 720s, 4 → 1440s, 5+ → 1800s.
    private async Task ApplyBackoffAsync(string errorType, CancellationToken ct)
    {
        _failureCount++;
        _currentInterval = (int)Math.Min(
            BaseIntervalSec * Math.Pow(2, _failureCount - 1),
            MaxIntervalSec);

        var backoff = new BackoffInfo(
            errorType,
            _failureCount,
            _currentInterval,
            DateTimeOffset.UtcNow.AddSeconds(_currentInterval));

        _state.SetBackoff(backoff);

        try
        {
            await _hub.Clients.All.SendAsync("backoffUpdated", backoff, ct);
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("Backoff: interval={Interval}s next={Next}",
            _currentInterval, backoff.NextAttemptAt);
    }

    // Surfaces the "token endpoint rate-limited -- waiting on host refresh" state WITHOUT ramping
    // the poll interval. Unlike ApplyBackoffAsync this holds the cadence at base so every cycle
    // promptly re-checks disk-adoption (the primary recovery path); the next poll, not a network
    // refresh, is what recovers us once the host writes a fresher token. The banner message comes
    // from the "refresh_rate_limited" errorType -- the frontend keeps it calm (no ramp/×2 noise).
    private async Task ApplyRefreshRateLimitedAsync(CancellationToken ct)
    {
        _currentInterval = BaseIntervalSec;   // no exponential ramp; keep re-checking disk often

        var info = new BackoffInfo(
            "refresh_rate_limited",
            _failureCount,                    // not incremented: this is a waiting state, not a fail-ramp
            _currentInterval,
            DateTimeOffset.UtcNow.AddSeconds(_currentInterval));

        _state.SetBackoff(info);

        try
        {
            await _hub.Clients.All.SendAsync("backoffUpdated", info, ct);
        }
        catch (OperationCanceledException) { }
    }
}
