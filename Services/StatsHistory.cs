using System.Globalization;
using System.Text.Json.Serialization;

namespace ClaudeUsage.Services;

public sealed record StatsPoint(DateTimeOffset T, double V,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool BreakBefore = false);
public sealed record StatsSeries(string Id, string Label, IReadOnlyList<StatsPoint> Points);
public sealed record StatsHistoryResponse(string Range, DateTimeOffset From, DateTimeOffset To,
    DateTimeOffset? Earliest, bool HasData, IReadOnlyList<StatsSeries> Series,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StatsPoint>? Session = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StatsPoint>? Week = null);

public static class StatsHistory
{
    public static StatsHistoryResponse Claude(UsageLog log, StatsWindow window)
    {
        var samples = log.ReadRange(window.From, window.To);
        StatsPoint[] Points(string key)
        {
            var observations = UsageLog.SeriesFor(samples, key);
            return CumulativeUsage.Compute(observations).Select((point, index) =>
                new StatsPoint(point.Time, point.Cumulative, index > 0 &&
                    point.Time - observations[index - 1].Time > TimeSpan.FromMinutes(15))).ToArray();
        }
        var session = Points("five_hour");
        var week = Points("seven_day");
        return new(window.Range, window.From, window.To, log.Earliest(),
            session.Length > 0 || week.Length > 0,
            [new("session", "5-hour", session), new("week", "Week", week)], session, week);
    }

    public static StatsHistoryResponse Codex(CodexQuotaLog log, StatsWindow window)
    {
        var history = log.Read(window);
        var series = new List<StatsSeries>();
        foreach (var duration in history.Samples.SelectMany(s => s.Windows)
                     .Select(w => w.LimitWindowSeconds).Distinct().Order())
        {
            var points = new List<StatsPoint>();
            CodexQuotaLog.Sample? previousSample = null;
            ClaudeUsage.Models.CodexWindow? previous = null;
            double cumulative = 0;
            foreach (var sample in history.Samples)
            {
                var current = sample.Windows.FirstOrDefault(w => w.LimitWindowSeconds == duration);
                if (current is null) { previous = null; previousSample = null; continue; }
                var broken = points.Count > 0 && (previous is null || previousSample is null ||
                    sample.FetchedAt - previousSample.FetchedAt > TimeSpan.FromMinutes(15) ||
                    current.UsedPercent < previous.UsedPercent ||
                    CycleChanged(previous.ResetAt, current.ResetAt, duration) ||
                    (previous.ResetAt is { } reset && previousSample.FetchedAt < reset &&
                        sample.FetchedAt >= reset));
                if (previous is not null && !broken)
                    cumulative += Math.Max(0, current.UsedPercent - previous.UsedPercent);
                points.Add(new(sample.FetchedAt, cumulative, broken));
                previous = current;
                previousSample = sample;
            }
            series.Add(new("duration-" + duration.ToString(CultureInfo.InvariantCulture), Label(duration), points));
        }
        return new(window.Range, window.From, window.To, history.Earliest,
            series.Any(s => s.Points.Count > 0), series);
    }

    private static bool CycleChanged(DateTimeOffset? previous, DateTimeOffset? current, long duration)
    {
        if (previous is null || current is null) return previous != current;
        // Relative reset timestamps may shift by a few seconds due to request latency.
        return Math.Abs((current.Value - previous.Value).TotalSeconds) > Math.Min(30, duration / 10d);
    }

    private static string Label(long seconds) => seconds switch
    {
        604800 => "Week",
        _ when seconds % 86400 == 0 => $"{seconds / 86400}-day",
        _ when seconds % 3600 == 0 => $"{seconds / 3600}-hour",
        _ when seconds % 60 == 0 => $"{seconds / 60}-minute",
        _ => $"{seconds}-second",
    };
}
