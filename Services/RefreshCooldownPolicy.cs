namespace ClaudeUsage.Services;

// Pure, dependency-free policy for the refresh cooldown applied after a 429 from the OAuth
// token endpoint. Kept separate from UsageService so it can be unit-tested in isolation --
// the test project links this file directly, the same pattern as CumulativeUsage.
//
// Rationale: once the access token is expired/within skew, the poller would re-fire a refresh
// every cycle; each attempt resets the endpoint's rate-limit clock, so the app locks itself
// out indefinitely. Cooling off breaks that loop.
internal static class RefreshCooldownPolicy
{
    internal const long BaseMs = 15L * 60 * 1000; // 15 minutes
    internal const long MaxMs  = 60L * 60 * 1000; // 60 minutes

    // Retry-After (when the server supplies it) wins, clamped to >= 0; otherwise Base doubled
    // per consecutive 429, capped at Max. consecutive429 is 1-based for the first 429.
    internal static long ComputeMs(int consecutive429, long? retryAfterMs)
    {
        if (retryAfterMs is { } ra) return Math.Max(ra, 0);
        var n      = Math.Max(consecutive429, 1);
        var scaled = BaseMs * (long)Math.Pow(2, n - 1);
        return Math.Min(scaled, MaxMs);
    }
}
