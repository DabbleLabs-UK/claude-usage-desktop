using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

// Covers the cumulative-positive-delta semantics:
//   normal increments · reset drop · flat samples · gaps in samples.
public class CumulativeUsageTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);

    // Build a series at fixed 180s spacing unless a custom step is given.
    private static List<UsageSample> Series(params double[] utils)
    {
        var list = new List<UsageSample>();
        for (int i = 0; i < utils.Length; i++)
            list.Add(new UsageSample(T0.AddSeconds(180 * i), utils[i]));
        return list;
    }

    private static double[] Cumulatives(IReadOnlyList<UsageSample> s) =>
        CumulativeUsage.Compute(s).Select(p => p.Cumulative).ToArray();

    [Fact]
    public void NormalIncrements_SumDeltasFromZeroBaseline()
    {
        // 10 -> 20 -> 35 : the first sample anchors at 0, then +10, +15.
        var s = Series(10, 20, 35);
        Assert.Equal(new double[] { 0, 10, 25 }, Cumulatives(s));
        Assert.Equal(25, CumulativeUsage.Total(s));
    }

    [Fact]
    public void ResetDrop_TreatedAsResetNotNegativeUsage()
    {
        // Climb to 95, window resets (drops to 5), climb again to 20.
        // The -90 drop contributes 0; counting resumes from 5.
        var s = Series(80, 95, 5, 20);
        Assert.Equal(new double[] { 0, 15, 15, 30 }, Cumulatives(s));
        Assert.Equal(30, CumulativeUsage.Total(s));
    }

    [Fact]
    public void MultipleWindowsInRange_CumulativeCanExceed100()
    {
        // Two full session windows back-to-back: 0->100, reset, 0->100 => 200% consumed.
        var s = Series(0, 50, 100, 0, 50, 100);
        Assert.Equal(200, CumulativeUsage.Total(s));
    }

    [Fact]
    public void FlatSamples_ContributeNothing()
    {
        var s = Series(50, 50, 50, 50);
        Assert.Equal(new double[] { 0, 0, 0, 0 }, Cumulatives(s));
        Assert.Equal(0, CumulativeUsage.Total(s));
    }

    [Fact]
    public void Gap_NoUsageFabricated_OnlyObservedDeltaCounts()
    {
        // App was off for hours between the two samples. We count only the real observed
        // positive change (+5), never a time-scaled fill of the gap.
        var s = new List<UsageSample>
        {
            new(T0,                  40),
            new(T0.AddHours(3),      45),
        };
        Assert.Equal(new double[] { 0, 5 }, Cumulatives(s));
        Assert.Equal(5, CumulativeUsage.Total(s));
    }

    [Fact]
    public void Gap_AcrossAReset_AssumesNoUsage()
    {
        // The app was off across a window reset: 90 then (gap) 10. The drop is a reset,
        // so nothing is counted -- we don't assume the climb-then-reset happened.
        var s = new List<UsageSample>
        {
            new(T0,             90),
            new(T0.AddHours(6), 10),
        };
        Assert.Equal(new double[] { 0, 0 }, Cumulatives(s));
        Assert.Equal(0, CumulativeUsage.Total(s));
    }

    [Fact]
    public void Empty_And_Single_AreWellDefined()
    {
        Assert.Empty(CumulativeUsage.Compute(new List<UsageSample>()));
        Assert.Equal(0, CumulativeUsage.Total(new List<UsageSample>()));

        var one = Series(42);
        Assert.Equal(new double[] { 0 }, Cumulatives(one)); // single sample = baseline only
        Assert.Equal(0, CumulativeUsage.Total(one));
    }
}
