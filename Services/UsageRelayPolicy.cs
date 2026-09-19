using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

public static class UsageRelayPolicy
{
    public static bool IsFresh(UsageData? data, DateTimeOffset now) =>
        data is { IsStale: false, Windows: not null } &&
        data.FetchedAt > now.AddMinutes(-10) && data.FetchedAt <= now.AddMinutes(1) &&
        data.Windows.Values.Any(w => w is not null) &&
        data.Windows.Values.All(w => w is null || (double.IsFinite(w.Utilization) && w.Utilization >= 0));
}

public sealed class UsageSourcesUnavailableException(string detail) : Exception(detail);
