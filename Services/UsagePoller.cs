using System.Net;
using System.Net.NetworkInformation;
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
    private readonly PollLog _pollLog;
    private readonly IHubContext<UsageHub> _hub;
    private readonly ILogger<UsagePoller> _logger;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    private int _failureCount    = 0;
    private int _currentInterval = BaseIntervalSec;

    public UsagePoller(
        UsageService usageService,
        UsageState state,
        UsageLog log,
        PollLog pollLog,
        IHubContext<UsageHub> hub,
        ILogger<UsagePoller> logger)
    {
        _usageService = usageService;
        _state = state;
        _log = log;
        _pollLog = pollLog;
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
        // Cheap pre-check: if the OS reports no usable network at all, don't even attempt the
        // poll (and don't ramp the backoff). This is the "(b) connectivity loss" case -- hold the
        // base cadence so we recover promptly when the link returns. NOTE: GetIsNetworkAvailable
        // returns true if ANY non-loopback adapter is up, so on a box with a virtual adapter this
        // rarely trips; the real offline detection is the null-StatusCode catch below.
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            _logger.LogWarning("No network available; skipping poll, holding base cadence.");
            _pollLog.LogFailure("no network available (OS reports link down); poll skipped");
            _state.MarkStale();
            await ApplyNoNetworkAsync(ct);
            if (_state.Current is { } offline)
                await _hub.Clients.All.SendAsync("usageUpdated", offline, ct);
            return;
        }

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
        catch (DeadLoginException)
        {
            // UsageService already classified this as a DEAD login and skipped all CLI/network
            // refresh attempts -- nothing is coming on its own. Surface a dedicated, honest
            // state instead of the "waiting on Claude Code to refresh" banner, and hold the base
            // cadence (no ramp) so the moment the user runs /login, the next cycle's disk read
            // picks up the fresh token immediately.
            _logger.LogWarning("Dead login detected (expiresAt==0 with refresh token present, or refresh token itself expired); awaiting manual /login.");
            _pollLog.LogFailure("dead login detected (expiresAt==0+refreshToken, or refreshTokenExpiresAt past); awaiting manual /login, no refresh attempted");
            _state.MarkStale();
            await ApplySignedOutAsync(ct);
            if (_state.Current is { } stale)
                await _hub.Clients.All.SendAsync("usageUpdated", stale, ct);
        }
        catch (RefreshRateLimitedException ex)
        {
            // Own token refresh is suppressed by the sticky 429 cooldown and disk had nothing
            // fresher to adopt. This is NOT an ordinary auth failure: we are deliberately not
            // poking the rate-limited token endpoint and are waiting for the host `claude` to
            // refresh on disk (the primary recovery path). Do NOT ramp the poll backoff -- keep
            // the base cadence so each cycle re-checks disk-adoption and recovers promptly.
            _logger.LogWarning("Token endpoint rate-limited; own refresh suppressed until {Until}. Polling continues at base cadence to await host disk refresh.", ex.SuppressedUntilMs);
            _pollLog.LogFailure($"refresh rate-limited (sticky 429 cooldown until {ex.SuppressedUntilMs}); awaiting host disk refresh");
            _state.MarkStale();
            await ApplyRefreshRateLimitedAsync(ct);
            if (_state.Current is { } stale)
                await _hub.Clients.All.SendAsync("usageUpdated", stale, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("401 -- auth error. Backing off (failure #{Count}).", _failureCount + 1);
            _pollLog.LogFailure($"401 auth error (refresh could not fix); backing off (failure #{_failureCount + 1})");
            _state.MarkStale();
            await ApplyBackoffAsync(ConnectivityState.AuthError, ct);
            if (_state.Current is { } stale)
                await _hub.Clients.All.SendAsync("usageUpdated", stale, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("429 -- rate limited. Backing off (failure #{Count}).", _failureCount + 1);
            _pollLog.LogFailure($"429 usage endpoint rate-limited; backing off (failure #{_failureCount + 1})");
            await ApplyBackoffAsync(ConnectivityState.RateLimited, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            // No HTTP response at all -- DNS failure, no route, connection refused, host
            // unreachable, TLS failure. This is local connectivity loss, NOT a server/auth fault,
            // so do NOT ramp the 30-min backoff and NEVER arm the sticky refresh cooldown. Hold the
            // base cadence and recover promptly once the link returns (a NetworkChange event also
            // fires an immediate re-probe).
            _logger.LogWarning("No network response ({Type}) -- offline; holding base cadence.", ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            _pollLog.LogFailure($"no HTTP response ({ex.InnerException?.GetType().Name ?? ex.GetType().Name}) -- offline; holding base cadence");
            _state.MarkStale();
            await ApplyNoNetworkAsync(ct);
            if (_state.Current is { } stale)
                await _hub.Clients.All.SendAsync("usageUpdated", stale, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not our shutdown token -- this is an HttpClient request timeout (TaskCanceledException
            // with no cancellation requested), another symptom of connectivity loss. Treat it like
            // offline: no ramp, base cadence.
            _logger.LogWarning("Poll timed out -- treating as offline; holding base cadence.");
            _pollLog.LogFailure("poll timed out (request cancellation, not shutdown) -- treating as offline");
            _state.MarkStale();
            await ApplyNoNetworkAsync(ct);
            if (_state.Current is { } stale)
                await _hub.Clients.All.SendAsync("usageUpdated", stale, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Catch-all for anything not network/rate-limit/401. After the network catches above,
            // the dominant real cause here is an unreadable/missing/malformed .credentials.json
            // (FileNotFound / InvalidOperation / JsonException from ReadCredentialsAsync) -- i.e. a
            // sign-in problem -- so route it through the AuthError grace-then-prompt path (which is
            // also where the original design documented 'other' should land). A genuinely odd
            // one-off self-heals on the next successful poll.
            _logger.LogError(ex, "Unexpected error fetching usage. Backing off (failure #{Count}).", _failureCount + 1);
            _pollLog.LogFailure($"unexpected error ({ex.GetType().Name}: {ex.Message}); backing off (failure #{_failureCount + 1})");
            await ApplyBackoffAsync(ConnectivityState.AuthError, ct);
        }
    }

    // Increments failure count, doubles interval (capped), updates state, pushes hub message.
    // Schedule: failure 1 → 180s, 2 → 360s, 3 → 720s, 4 → 1440s, 5+ → 1800s.
    private async Task ApplyBackoffAsync(ConnectivityState state, CancellationToken ct)
    {
        _failureCount++;
        _currentInterval = (int)Math.Min(
            BaseIntervalSec * Math.Pow(2, _failureCount - 1),
            MaxIntervalSec);

        var backoff = new BackoffInfo(
            state,
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
    // promptly re-reads the credentials file (the primary recovery path); the next poll, not a
    // network refresh, is what recovers us once the host writes a fresher token. State RateLimited
    // keeps the frontend banner calm (no ramp noise).
    private async Task ApplyRefreshRateLimitedAsync(CancellationToken ct)
    {
        _currentInterval = BaseIntervalSec;   // no exponential ramp; keep re-checking disk often

        var info = new BackoffInfo(
            ConnectivityState.RateLimited,
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

    // Surfaces the honest "signed out of Claude Code -- run /login to reconnect" state for a DEAD
    // login (see LoginStatePolicy/DeadLoginException). Like the other waiting states this does
    // NOT ramp the interval -- the fix is entirely out of the app's hands until the user signs in
    // again, and holding the base cadence means the very next poll after that picks the fresh
    // token straight up off disk with no extra delay.
    private async Task ApplySignedOutAsync(CancellationToken ct)
    {
        _currentInterval = BaseIntervalSec;   // no exponential ramp; recover promptly after /login

        var info = new BackoffInfo(
            ConnectivityState.SignedOut,
            _failureCount,                    // not incremented: a waiting state, not a fail-ramp
            _currentInterval,
            DateTimeOffset.UtcNow.AddSeconds(_currentInterval));

        _state.SetBackoff(info);

        try
        {
            await _hub.Clients.All.SendAsync("backoffUpdated", info, ct);
        }
        catch (OperationCanceledException) { }
    }

    // Surfaces the calm "offline -- will reconnect when your connection returns" state for local
    // connectivity loss. Like ApplyRefreshRateLimitedAsync this does NOT ramp the interval and
    // does NOT touch the failure count: a network blip is not a server/auth fault, so we hold the
    // base cadence and recover promptly once the link is back (a NetworkChange event also pokes an
    // immediate poll). State NoNetwork drives the frontend's offline banner.
    private async Task ApplyNoNetworkAsync(CancellationToken ct)
    {
        _currentInterval = BaseIntervalSec;   // no exponential ramp; reconnect fast when link returns

        var info = new BackoffInfo(
            ConnectivityState.NoNetwork,
            _failureCount,                    // not incremented: a waiting state, not a fail-ramp
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
