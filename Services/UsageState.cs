using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

public sealed class UsageState
{
    private volatile UsageData? _current;

    public UsageData? Current => _current;

    public void Update(UsageData data) => _current = data;

    public void MarkStale()
    {
        if (_current is { } c)
            _current = c with { IsStale = true };
    }
}
