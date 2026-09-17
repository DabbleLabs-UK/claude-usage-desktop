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
    private readonly IHubContext<UsageHub> _hub;
    private readonly ILogger<AgentSpendPoller> _logger;

    public AgentSpendPoller(AgentSpendState state, IHubContext<UsageHub> hub, ILogger<AgentSpendPoller> logger)
    {
        _state = state;
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
            var result = AgentSpendReader.Read();

            if (result.Status == AgentSpendReader.ReadStatus.Ok
                && AgentSpendFreshnessPolicy.ShouldAdopt(result.Data!.Source.Status))
            {
                var fresh = AgentSpendFreshnessPolicy.IsFreshEnough(result.Data.GeneratedAt, DateTimeOffset.UtcNow);
                _state.Update(result.Data with { IsStale = !fresh });
            }
            else
            {
                // Missing file, malformed JSON, or source.status != "ok" -- keep whatever snapshot
                // we already have (if any) and flag it stale. Never zeroes figures: MarkStale is a
                // no-op when there is nothing to keep yet, so the card just stays hidden until the
                // first good read.
                _state.MarkStale();
            }

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
            _logger.LogWarning(ex, "Agent-spend poll cycle failed unexpectedly; other usage lanes unaffected.");
        }
    }
}
