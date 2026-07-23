using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// In-memory holder for the Codex lane, mirroring UsageState but kept SEPARATE so Codex and Claude
// never share mutable state. Current is the last usage snapshot; Status is the last connectivity/
// auth state. Both volatile; the single CodexPoller writes them sequentially.
public sealed class CodexUsageState
{
    private volatile CodexUsageData? _current;
    private volatile CodexStatus? _status;

    public CodexUsageData? Current => _current;
    public CodexStatus? Status => _status;

    public void Update(CodexUsageData data) => _current = data;

    public void SetStatus(CodexStatus status) => _status = status;

    public void MarkStale()
    {
        if (_current is { } c)
            _current = c with { IsStale = true };
    }
}
