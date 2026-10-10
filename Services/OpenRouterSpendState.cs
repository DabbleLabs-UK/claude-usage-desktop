using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Last-known values survive a transient export failure, but never cross account boundaries.
public sealed class OpenRouterSpendState
{
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    private volatile OpenRouterSpendSnapshot _current = new("notConfigured", null);
    private string? _accountScopeId;
    private bool _seenFile;

    public OpenRouterSpendSnapshot Current => _current;

    public void Apply(OpenRouterSpendReader.ReadResult result, DateTimeOffset now)
    {
        if (result.Status != OpenRouterSpendReader.ReadStatus.Missing) _seenFile = true;

        if (result.ClearCachedData)
        {
            _accountScopeId = null;
            _current = new("unavailable", null);
        }

        if (result.AccountScopeId is { } identity && identity != _accountScopeId)
        {
            _accountScopeId = identity;
            _current = new("unavailable", null);
        }

        if (result.Data is { } data)
        {
            var anchor = data.Source.LastSuccessAt ?? data.GeneratedAt;
            // Copy/sync file timestamps do not prove an upstream refresh. Reject implausible
            // future snapshots instead of treating their age as zero indefinitely.
            if (anchor <= now + FutureTolerance && data.GeneratedAt <= now + FutureTolerance)
            {
                var previous = _current.Data;
                var previousAnchor = previous?.Source.LastSuccessAt ?? previous?.GeneratedAt;
                if (previousAnchor is null || anchor >= previousAnchor)
                {
                    var fresh = result.Status == OpenRouterSpendReader.ReadStatus.Ok
                        && now - anchor <= FreshWindow && now - data.GeneratedAt <= FreshWindow;
                    _current = new(fresh ? "live" : "stale", data with { IsStale = !fresh });
                    return;
                }
            }
        }

        _current = _current.Data is { } cached
            ? new("stale", cached with { IsStale = true })
            : new(_seenFile ? "unavailable" : "notConfigured", null);
    }
}
