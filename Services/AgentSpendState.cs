using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// In-memory holder for the API-spend lane, mirroring CodexUsageState so all three usage lanes
// (Claude, Codex, agent spend) keep fully separate mutable state. Current is null until the first
// successful (status=="ok") read -- the frontend hides the whole card until then, exactly like
// Codex's NoToken treatment, instead of showing a zeroed placeholder.
public sealed class AgentSpendState
{
    private volatile AgentSpendData? _current;

    public AgentSpendData? Current => _current;

    public void Update(AgentSpendData data) => _current = data;

    public void MarkStale()
    {
        if (_current is { } c)
            _current = c with { IsStale = true };
    }
}
