namespace ClaudeUsage.Services;

// Pure, dependency-free policy for the refresh cooldown applied after a 429 from the OAuth
// token endpoint. Kept separate from UsageService so it can be unit-tested in isolation --
// the test project links this file directly, the same pattern as CumulativeUsage.
//
// WHY THE COOLDOWN IS NOW MULTI-HOUR (the v0.1.18 self-lockout post-mortem):
// Live probing of platform.claude.com / console.anthropic.com showed the token endpoint
// returns 429 {"type":"rate_limit_error"} *before* it validates the token (a GARBAGE token
// 429s identically), with NO Retry-After header, and the block is sticky: it re-extends on
// every hit and does NOT clear by waiting through 15/30/60-min spaced retries. So the OLD
// policy -- base 15min, doubling, capped at 60min -- expired *inside* the server's still-open
// penalty window and re-armed it on the next poll, locking the app out indefinitely.
//
// The fix: a no-Retry-After 429 triggers a LONG sticky cooldown (multi-hour, comparable to the
// ~8h access-token lifetime) so the app stops poking the endpoint at all. Recovery is meant to
// come from DISK-ADOPTION of the host `claude` refresh (residential IP, which works), NOT from
// this timer -- TryAdoptFresherDiskTokenAsync runs every poll cycle, including during cooldown.
// This timer only bounds the worst case where the host never refreshes for hours.
//
// KNOWN LIMITATION: from a VM/datacenter IP the app's OWN refresh may be throttled far harder
// than residential `claude`, so a pure-idle own-network-refresh may be effectively unprovable
// here. That is precisely why disk-adoption is primary and own-refresh is a rare last resort.
internal static class RefreshCooldownPolicy
{
    internal const long StickyBaseMs = 4L * 60 * 60 * 1000;  // 4 hours  (first no-Retry-After 429)
    internal const long StickyMaxMs  = 12L * 60 * 60 * 1000; // 12 hours (ramp cap)

    // Retry-After (when the server supplies it) is authoritative and honoured exactly, clamped
    // >= 0 -- the server's wait wins, even if shorter or much longer than our sticky window.
    //
    // When Retry-After is ABSENT (the observed real-world case) apply the sticky multi-hour
    // cooldown: StickyBase doubled per consecutive 429, capped at StickyMax. consecutive429 is
    // 1-based for the first 429 (so n=1 -> 4h, n=2 -> 8h, n>=3 -> 12h).
    internal static long ComputeMs(int consecutive429, long? retryAfterMs)
    {
        if (retryAfterMs is { } ra) return Math.Max(ra, 0);
        var n      = Math.Max(consecutive429, 1);
        var scaled = StickyBaseMs * (long)Math.Pow(2, n - 1);
        return Math.Min(scaled, StickyMaxMs);
    }
}
