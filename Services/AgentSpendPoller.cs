using ClaudeUsage.Hubs;
using ClaudeUsage.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeUsage.Services;

// Background reader for the Agents & Inference API-spend lane. Deliberately its OWN
// BackgroundService, separate from UsagePoller/CodexPoller: a bad/missing/malformed spend-summary
// file can only ever update agent-spend state and push agent-spend hub messages, so it can NEVER
// stall or mark stale either the Claude or Codex poll (or vice versa). There is no network call
// and no auth here -- just a periodic re-read of a JSON file Agents & Inference publishes on its
// own schedule -- so this is simpler than either of the other pollers.
public sealed class AgentSpendPoller : BackgroundService
{
    private const int IntervalSec = 180;

    private readonly AgentSpendState _state;
    private readonly PollLog _pollLog;
    private readonly IHubContext<UsageHub> _hub;
    private readonly ILogger<AgentSpendPoller> _logger;

    public AgentSpendPoller(AgentSpendState state, PollLog pollLog, IHubContext<UsageHub> hub, ILogger<AgentSpendPoller> logger)
    {
        _state = state;
        _pollLog = pollLog;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Proves (in poll.log, which is readable in a published/dist build -- ILogger has no
        // providers outside DEBUG) that this hosted service actually started and exactly which
        // path it resolved to watch, before any poll has happened.
        _pollLog.LogAgentSpendStartup(AgentSpendReader.FilePath);

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
            var result = AgentSpendReader.Read();

            var adopted = false;
            var stale = true;
            if (result.Status == AgentSpendReader.ReadStatus.Ok
                && AgentSpendFreshnessPolicy.ShouldAdopt(result.Data!.Source.Status))
            {
                // generated_at advances only when a launcher run changes the remote summary. The
                // local file timestamp advances on every successful five-minute sync, including
                // quiet periods, so it is the correct reachability/freshness signal here.
                var freshnessAnchor = result.FileUpdatedAt ?? result.Data.GeneratedAt;
                var fresh = AgentSpendFreshnessPolicy.IsFreshEnough(freshnessAnchor, DateTimeOffset.UtcNow);
                _state.Update(result.Data with { IsStale = !fresh });
                adopted = true;
                stale = !fresh;
            }
            else
            {
                // Missing file, malformed JSON, or source.status != "ok" -- keep whatever snapshot
                // we already have (if any) and flag it stale. Never zeroes figures: MarkStale is a
                // no-op when there is nothing to keep yet, so the card just stays hidden until the
                // first good read.
                _state.MarkStale();
            }

            _pollLog.LogAgentSpendPoll(
                AgentSpendReader.FilePath, result.FileExisted, result.Status.ToString(), result.Detail, adopted, stale);

            if (_state.Current is { } current)
                await _hub.Clients.All.SendAsync("agentSpendUpdated", current, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Backstop: an agent-spend fault must NEVER escape into the host or disturb Claude/Codex.
            // Logged to BOTH ILogger (dev/DEBUG visibility) and poll.log (the only one visible in a
            // published/dist build) so this can never go silent the way the original report did.
            _logger.LogWarning(ex, "Agent-spend poll cycle failed unexpectedly; other usage lanes unaffected.");
            try { _pollLog.LogAgentSpendPoll(AgentSpendReader.FilePath, false, "PollerException", $"{ex.GetType().Name}: {ex.Message}", false, true); }
            catch { /* logging is best-effort */ }
        }
    }
}
