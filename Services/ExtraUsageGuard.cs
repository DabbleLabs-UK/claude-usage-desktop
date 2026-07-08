using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Pure guard against a spurious extra_usage=0 reading from the usage API. Kept separate from
// UsageService so it can be unit-tested in isolation -- the test project links this file
// directly, the same pattern as CumulativeUsage / RefreshCooldownPolicy.
//
// This is a DIFFERENT failure mode than a window's mid-period reset (see windowResetState in
// wwwroot/index.html): Anthropic genuinely zeroes a WINDOW's counter mid-period without
// advancing resets_at, and that reading is real data -- rendered as-is, not held. extra_usage
// instead shows poll.log evidence of a single bad SAMPLE: it reads 0 on one poll and bounces
// straight back to the SAME prior value on the very next poll, while the window data stays
// present and climbs smoothly across the same polls. That is a transient glitch, not a state
// change, so here (unlike windows) the fix is to hold the last-known value rather than render it.
//
// extra_usage carries no resets_at of its own (see ExtraUsageData / UsageService.MapExtra), so
// there is no per-field boundary to check directly. The credits are a MONTHLY allowance, so the
// best available corroboration for a genuine reset is a UTC calendar-month boundary crossing
// since the last known-good (non-held) reading -- anything reading 0 mid-month, with window data
// present, is treated as a bad sample.
internal static class ExtraUsageGuard
{
    private const double ZeroEps = 0.0001;

    // Value: what to actually serve (state/UI/history) for this poll.
    // Held: true iff Value is the carried-over last-known-good reading, not this poll's own.
    // UpdateBaseline: true iff Value should become the new last-known-good going forward.
    internal readonly record struct Result(ExtraUsageData? Value, bool Held, bool UpdateBaseline);

    // incoming/incomingAt: this poll's parsed extra_usage and fetchedAt.
    // hasWindowData: true iff this poll's Windows dictionary is non-empty -- corroborates a
    // normal, otherwise-successful poll (vs. a broken/empty response, where 0 proves nothing).
    // lastGood: the last known-good (accepted, non-held) reading plus when it was observed;
    // null before any accepted reading exists this run.
    internal static Result Apply(
        ExtraUsageData? incoming,
        bool hasWindowData,
        (ExtraUsageData Data, DateTimeOffset At)? lastGood,
        DateTimeOffset incomingAt)
    {
        if (incoming is null)
            return new Result(null, false, false);

        var looksZero = incoming.UsedCredits <= ZeroEps && incoming.Utilization <= ZeroEps;
        if (!looksZero)
            return new Result(incoming, false, true); // real non-zero reading -- trust it outright

        if (lastGood is null)
            return new Result(incoming, false, true); // no baseline to compare against yet

        if (!hasWindowData)
            return new Result(incoming, false, false); // degraded poll -- not corroborated either way

        var prev = lastGood.Value.At.UtcDateTime;
        var now  = incomingAt.UtcDateTime;
        if (now.Year != prev.Year || now.Month != prev.Month)
            return new Result(incoming, false, true); // month boundary crossed -- genuine reset

        // Mid-month, windows present, was previously non-zero: a bad reading. Hold last-known.
        return new Result(lastGood.Value.Data, true, false);
    }
}
