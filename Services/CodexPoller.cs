using ClaudeUsage.Hubs;
using ClaudeUsage.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeUsage.Services;

// Background poller for the Codex lane. Deliberately its OWN BackgroundService, separate from
// UsagePoller: a Codex auth failure, network error or endpoint change updates only Codex state
// and pushes only Codex hub messages, so it can NEVER stall or mark stale the Claude poll (or
// vice versa). It is much simpler than UsagePoller -- there is no refresh / cooldown / backoff
// machinery, because we never refresh Codex tokens; a fixed base cadence with graceful
// degradation to a typed CodexState is all that is needed. When there is no Codex login the
// cycle is a cheap local file check (no network call), so a later `codex login` surfaces
// automatically within one interval.
public sealed class CodexPoller : BackgroundService
{
    private const int IntervalSec = 180;

    private readonly CodexUsageService _service;
    private readonly CodexUsageState _state;
    private readonly PollLog _pollLog;
    private readonly IHubContext<UsageHub> _hub;
    private readonly ILogger<CodexPoller> _logger;

    public CodexPoller(
        CodexUsageService service,
        CodexUsageState state,
        PollLog pollLog,
        IHubContext<UsageHub> hub,
        ILogger<CodexPoller> logger)
    {
        _service = service;
        _state = state;
        _pollLog = pollLog;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromSeconds(IntervalSec), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            // FetchAsync never throws (it catches its own faults and returns a typed result), so a
            // Codex-lane problem can't bubble up and disturb anything else.
            var result = await _service.FetchAsync();
            _state.SetStatus(new CodexStatus(result.State));

            // NOTE ordering: the hub broadcast happens BEFORE the poll.log write. poll.log is the
            // one thing the two lanes share (a single file + lock), so logging first would let a
            // slow write on this lane briefly delay the other lane's UI update. Broadcasting first
            // keeps the lanes independent on the path that users actually see.
            if (result.State == CodexState.Live && result.Data is { } data)
            {
                _state.Update(data);
                await _hub.Clients.All.SendAsync("codexUsageUpdated", data, ct);
                await _hub.Clients.All.SendAsync("codexStatusUpdated", _state.Status, ct);
                _pollLog.LogCodexSuccess(result.Source, data);
            }
            else
            {
                // Keep the last-known snapshot (dimmed/stale) so a transient miss doesn't blank the
                // Codex column; NoToken has no snapshot to keep and the UI hides the section.
                _state.MarkStale();
                if (_state.Current is { } stale)
                    await _hub.Clients.All.SendAsync("codexUsageUpdated", stale, ct);
                await _hub.Clients.All.SendAsync("codexStatusUpdated", _state.Status, ct);
                _pollLog.LogCodexFailure($"{result.State} src=\"{result.Source}\": {result.Detail}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Backstop: a Codex-lane fault must NEVER escape into the host or disturb Claude.
            _logger.LogWarning(ex, "Codex poll cycle failed unexpectedly; Claude polling unaffected.");
            try { _pollLog.LogCodexFailure($"poller exception {ex.GetType().Name}: {ex.Message}"); }
            catch { /* logging is best-effort */ }
        }
    }
}
