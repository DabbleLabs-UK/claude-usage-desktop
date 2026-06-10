namespace ClaudeUsage.Services;

// A single observed utilization reading for one usage window.
public readonly record struct UsageSample(DateTimeOffset Time, double Utilization);

// A computed cumulative-consumption point: the running total of POSITIVE deltas
// up to and including this sample's time.
public readonly record struct CumulativePoint(DateTimeOffset Time, double Cumulative);

// Converts a series of raw window utilization readings into a cumulative-consumption
// series.
//
// Semantics (see the feature spec):
//   * utilization is a whole-integer %; it climbs as the window is consumed and DROPS
//     toward 0 when the window resets.
//   * Cumulative use is the sum of POSITIVE deltas only. A drop between two consecutive
//     samples means the window reset (or we're looking across a reset), NOT negative
//     usage -- it contributes 0, and counting resumes from the new lower value.
//   * Gaps in samples (the app was off) add no usage on their own: we only ever count
//     the positive change actually observed between two real samples. We never fabricate
//     usage to fill a time gap, and we never time-interpolate.
//
// The series is purely sample-driven (not time-driven): the cumulative at point i depends
// only on the utilization values, never on how far apart in wall-clock the samples sit.
// The first point anchors the baseline at 0 (we count consumption observed within the
// supplied range, not whatever was already used before it started).
public static class CumulativeUsage
{
    // Samples MUST be ordered by time ascending. Returns a point per input sample.
    public static List<CumulativePoint> Compute(IReadOnlyList<UsageSample> samples)
    {
        var result = new List<CumulativePoint>(samples.Count);
        double cumulative = 0;
        double? previous = null;

        foreach (var s in samples)
        {
            if (previous is double prev)
            {
                var delta = s.Utilization - prev;
                if (delta > 0) cumulative += delta;   // climb = consumption; drop/flat = 0
            }
            result.Add(new CumulativePoint(s.Time, cumulative));
            previous = s.Utilization;
        }

        return result;
    }

    // Convenience: the final cumulative total over the whole series (0 when empty).
    // Because the series is monotonically non-decreasing this is also its maximum.
    public static double Total(IReadOnlyList<UsageSample> samples)
    {
        var points = Compute(samples);
        return points.Count == 0 ? 0 : points[^1].Cumulative;
    }
}
