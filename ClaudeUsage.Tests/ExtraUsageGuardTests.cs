using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

// Covers the extra_usage spurious-zero guard: poll.log evidence showed extra_usage occasionally
// reading 0% for a single poll and bouncing straight back to the same prior value on the next
// one, while window data stayed present and climbed smoothly across the same polls. Distinct
// from a window's mid-period reset (real data, rendered as-is) -- this is a bad SAMPLE, so the
// guard holds the last-known value unless the 0 is corroborated by a UTC calendar-month boundary
// crossing (the credits are a monthly allowance with no resets_at of their own to check).
public class ExtraUsageGuardTests
{
    private static ExtraUsageData Extra(double usedCredits, double monthlyLimit = 50) =>
        new(MonthlyLimit: monthlyLimit, UsedCredits: usedCredits,
            Utilization: usedCredits / monthlyLimit * 100, Currency: "GBP",
            RawMonthlyLimit: monthlyLimit * 100, RawUsedCredits: usedCredits * 100);

    private static readonly DateTimeOffset MidJune = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LateJune = new(2026, 6, 30, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EarlyJuly = new(2026, 7, 1, 0, 5, 0, TimeSpan.Zero);

    [Fact]
    public void SpuriousZero_MidMonth_WindowsPresent_HoldsLastKnownValue()
    {
        var lastGood = (Extra(7.725), MidJune);
        var result = ExtraUsageGuard.Apply(Extra(0), hasWindowData: true, lastGood, LateJune);

        Assert.True(result.Held);
        Assert.False(result.UpdateBaseline);
        Assert.Equal(7.725, result.Value!.UsedCredits);
    }

    [Fact]
    public void GenuineReset_MonthBoundaryCrossed_AcceptsZero()
    {
        var lastGood = (Extra(7.725), LateJune);
        var result = ExtraUsageGuard.Apply(Extra(0), hasWindowData: true, lastGood, EarlyJuly);

        Assert.False(result.Held);
        Assert.True(result.UpdateBaseline);
        Assert.Equal(0, result.Value!.UsedCredits);
    }

    [Fact]
    public void DegradedPoll_NoWindowData_PassesThroughWithoutUpdatingBaseline()
    {
        var lastGood = (Extra(7.725), MidJune);
        var result = ExtraUsageGuard.Apply(Extra(0), hasWindowData: false, lastGood, LateJune);

        Assert.False(result.Held);
        Assert.False(result.UpdateBaseline);
        Assert.Equal(0, result.Value!.UsedCredits); // not held -- but also not trusted as a new baseline
    }

    [Fact]
    public void NoBaselineYet_FirstReadingAcceptedWhateverItIs()
    {
        var result = ExtraUsageGuard.Apply(Extra(0), hasWindowData: true, lastGood: null, MidJune);

        Assert.False(result.Held);
        Assert.True(result.UpdateBaseline);
        Assert.Equal(0, result.Value!.UsedCredits);
    }

    [Fact]
    public void RealNonZeroReading_AlwaysTrustedAndUpdatesBaseline()
    {
        var lastGood = (Extra(7.725), MidJune);
        var result = ExtraUsageGuard.Apply(Extra(8.1), hasWindowData: true, lastGood, LateJune);

        Assert.False(result.Held);
        Assert.True(result.UpdateBaseline);
        Assert.Equal(8.1, result.Value!.UsedCredits);
    }

    [Fact]
    public void NullExtraUsage_PassesThroughWithoutTouchingBaseline()
    {
        var lastGood = (Extra(7.725), MidJune);
        var result = ExtraUsageGuard.Apply(null, hasWindowData: true, lastGood, LateJune);

        Assert.False(result.Held);
        Assert.False(result.UpdateBaseline);
        Assert.Null(result.Value);
    }
}
