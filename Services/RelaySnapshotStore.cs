using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Written only by the background poller; HTTP requests cannot start paid calls or token refreshes.
public sealed class RelaySnapshotStore
{
    private UsageData? _lastGood;
    private readonly object _gate = new();

    public void Success(UsageData data)
    {
        lock (_gate) _lastGood = data;
    }

    public void Failure()
    {
        lock (_gate)
            if (_lastGood is not null) _lastGood = _lastGood with { IsStale = true };
    }

    public UsageData? Read(DateTimeOffset now)
    {
        lock (_gate)
            return UsageRelayPolicy.IsFresh(_lastGood, now) ? _lastGood : null;
    }
}
