using System.Text;
using System.Text.Json;
using ClaudeUsage.Models;
using Microsoft.Extensions.Logging;

namespace ClaudeUsage.Services;

// A logged sample read back from the JSONL store: its UTC time plus the utilization
// of every window present in that sample, keyed by the ORIGINAL API field name
// (five_hour, seven_day, seven_day_opus, ...). Windows that were null/absent in the
// sample are simply not present in the dictionary -- a gap for that window.
public sealed record LoggedSample(DateTimeOffset Time, IReadOnlyDictionary<string, double> Utilizations);

// Append-only, monthly-partitioned usage log.
//
//   %APPDATA%\ClaudeUsage\usage-log\YYYY-MM.jsonl   (one JSON object per line)
//
// Each line is the generic UsageData parse, serialized verbatim, so every top-level
// window (and extra_usage) the API returned is persisted -- future consumers choose
// what to read. Writes are append-only and best-effort: a failure NEVER propagates to
// the poller, and a torn final line from a crash is tolerated on read (skipped), so the
// already-written history can never be corrupted.
public sealed class UsageLog
{
    private readonly string LogDir;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Read side of WriteOpts: the camelCase records round-trip back into UsageData. Case-insensitive
    // so a hand-edited or older-format line still loads rather than silently dropping fields.
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<UsageLog> _logger;
    private readonly object _writeLock = new();

    public UsageLog(ILogger<UsageLog> logger) : this(logger, MonthlyUsageFiles.DefaultDirectory) { }

    public UsageLog(ILogger<UsageLog> logger, string directory)
    {
        _logger = logger;
        LogDir = directory;
    }

    private string FilePathFor(DateTimeOffset utc) =>
        Path.Combine(LogDir, $"{utc.UtcDateTime:yyyy-MM}.jsonl");

    // Append one sample. Called only on a SUCCESSFUL poll (never on failure). The whole
    // operation is guarded so a logging fault can't take the poller down.
    public void Append(UsageData data)
    {
        try
        {
            var utc  = data.FetchedAt.ToUniversalTime();
            var json = JsonSerializer.Serialize(data, WriteOpts);

            // Build the full record (line + newline) as a single byte buffer and write it
            // in one call so an interrupted write can at worst leave a trailing partial
            // line -- never a half-overwritten earlier line. Append mode + a process-wide
            // lock keeps concurrent writes from interleaving.
            // A leading separator also isolates any torn final line from an earlier crash.
            var bytes = Encoding.UTF8.GetBytes("\n" + json + "\n");

            lock (_writeLock)
            {
                Directory.CreateDirectory(LogDir);
                using var fs = new FileStream(
                    FilePathFor(utc), FileMode.Append, FileAccess.Write, FileShare.Read);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Usage-log append failed ({Type}); sample dropped.", ex.GetType().Name);
        }
    }

    // Read every logged sample whose timestamp falls in [from, to], ordered ascending.
    // Reads only the monthly files spanning the range. Unparseable / torn lines are
    // skipped silently. A missing folder or file yields no samples (not an error).
    public IReadOnlyList<LoggedSample> ReadRange(DateTimeOffset from, DateTimeOffset to)
    {
        var samples = new List<LoggedSample>();
        if (to < from) return samples;

        foreach (var path in MonthlyUsageFiles.Between(LogDir, from, to))
        {
            foreach (var line in MonthlyUsageFiles.Lines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (TryParseLine(line, from, to, out var sample))
                    samples.Add(sample);
            }
        }

        return samples.GroupBy(sample => sample.Time).Select(group => group.Last())
            .OrderBy(sample => sample.Time).ToArray();
    }

    public DateTimeOffset? Earliest()
    {
        // Re-read after reset rather than retaining a stale in-memory earliest timestamp.
        foreach (var path in MonthlyUsageFiles.All(LogDir))
        {
            DateTimeOffset? earliest = null;
            foreach (var line in MonthlyUsageFiles.Lines(path))
                if (TryParseLine(line, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, out var sample) &&
                    (sample.Utilizations.ContainsKey("five_hour") || sample.Utilizations.ContainsKey("seven_day")) &&
                    (earliest is null || sample.Time < earliest)) earliest = sample.Time;
            if (earliest is not null) return earliest;
        }
        return null;
    }

    // Returns the most recently persisted sample as a full UsageData (windows WITH resets_at,
    // extra_usage, fetchedAt), or null if nothing is logged / nothing parses. Used to seed the
    // in-memory state on relaunch so the UI can show last-known values (dimmed) before -- or
    // instead of -- the first successful poll, rather than going blank. Best-effort: scans the
    // newest monthly file's last good line first, walking back through older months if needed,
    // and never throws.
    public UsageData? ReadLastSample()
    {
        try
        {
            if (!Directory.Exists(LogDir)) return null;

            var files = Directory.GetFiles(LogDir, "*.jsonl");
            if (files.Length == 0) return null;
            Array.Sort(files, StringComparer.Ordinal); // yyyy-MM names sort chronologically

            for (var i = files.Length - 1; i >= 0; i--) // newest month first
            {
                UsageData? latest = null;
                foreach (var line in MonthlyUsageFiles.Lines(files[i]))
                {
                    try
                    {
                        var data = JsonSerializer.Deserialize<UsageData>(line, ReadOpts);
                        if (data?.Windows is not null && data.FetchedAt != default &&
                            (latest is null || data.FetchedAt >= latest.FetchedAt)) latest = data;
                    }
                    catch (JsonException) { /* keep the newest intact record */ }
                }
                if (latest is not null) return latest;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Usage-log ReadLastSample failed ({Type}); no seed.", ex.GetType().Name);
            return null;
        }
    }

    // Builds a utilization series for one window key, ready for CumulativeUsage.Compute.
    // Only samples that actually contain the key are included (absent = gap).
    public static List<UsageSample> SeriesFor(IReadOnlyList<LoggedSample> samples, string windowKey)
    {
        var series = new List<UsageSample>(samples.Count);
        foreach (var s in samples)
            if (s.Utilizations.TryGetValue(windowKey, out var util))
                series.Add(new UsageSample(s.Time, util));
        return series;
    }

    private static bool TryParseLine(string line, DateTimeOffset from, DateTimeOffset to, out LoggedSample sample)
    {
        sample = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("fetchedAt", out var ts) || ts.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(ts.GetString(), out var time))
                return false;

            if (time < from || time > to) return false;

            var utils = new Dictionary<string, double>(StringComparer.Ordinal);
            if (root.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in windows.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("utilization", out var u)
                        && u.ValueKind == JsonValueKind.Number && u.TryGetDouble(out var value)
                        && double.IsFinite(value) && value >= 0)
                        utils[prop.Name] = value;
                }
            }

            sample = new LoggedSample(time, utils);
            return true;
        }
        catch (JsonException)
        {
            return false; // torn / malformed line -- skip
        }
    }
}
