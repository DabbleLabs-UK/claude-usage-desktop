using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

public sealed class UsageState
{
    private volatile UsageData? _current;
    private volatile BackoffInfo? _backoff;

    public UsageData?   Current => _current;
    public BackoffInfo? Backoff  => _backoff;

    public void Update(UsageData data)
    {
        if (!data.IsStale) _backoff = null;
        _current = data;
    }

    public void SetBackoff(BackoffInfo? info)
    {
        if (info is not null) MarkStale();
        _backoff = info;
    }

    public void MarkStale()
    {
        if (_current is { } c)
            _current = c with { IsStale = true };
    }
}
