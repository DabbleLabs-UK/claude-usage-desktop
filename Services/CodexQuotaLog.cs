using System.Text;
using System.Text.Json;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Only quota observations are persisted. Account hashes select separate private directories;
// neither the hash, credentials nor credit balances are part of a graph response or log record.
public sealed class CodexQuotaLog
{
    public sealed record Sample(DateTimeOffset FetchedAt, IReadOnlyList<CodexWindow> Windows);
    public sealed record History(IReadOnlyList<Sample> Samples, DateTimeOffset? Earliest);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly Func<string?> _accountHash;
    private readonly object _writeLock = new();

    public CodexQuotaLog(string directory, Func<string?> accountHash)
    {
        _directory = directory;
        _accountHash = accountHash;
    }

    private string? AccountDirectory(string? accountHash) =>
        accountHash is { Length: 64 } && accountHash.All(Uri.IsHexDigit)
            ? Path.Combine(_directory, "codex", accountHash.ToUpperInvariant()) : null;

    private static bool Valid(CodexWindow? window) => window is not null &&
        window.LimitWindowSeconds > 0 && double.IsFinite(window.UsedPercent) &&
        window.UsedPercent >= 0 && window.UsedPercent <= 100;

    private static CodexWindow[] UnambiguousWindows(IEnumerable<CodexWindow> windows) =>
        windows.Where(Valid).GroupBy(w => w.LimitWindowSeconds)
            .Select(group => group.Distinct().Take(2).ToArray())
            .Where(group => group.Length == 1).Select(group => group[0]).ToArray();

    public void Append(CodexUsageData data, string? accountHash)
    {
        try
        {
            var directory = AccountDirectory(accountHash);
            if (directory is null || data.IsStale) return;
            // The source model has no stable lane identity beyond duration. Conflicting
            // same-duration windows are unknown rather than an arbitrary selected reading.
            var windows = UnambiguousWindows(data.Windows);
            // Keep fresh empty snapshots too: they show that a previously reported window
            // was missing, so later observations must start a new segment.
            var sample = new Sample(data.FetchedAt.ToUniversalTime(), windows);
            // The leading separator isolates an incomplete final line left by a crash.
            var bytes = Encoding.UTF8.GetBytes("\n" + JsonSerializer.Serialize(sample, JsonOptions) + "\n");
            lock (_writeLock)
            {
                Directory.CreateDirectory(directory);
                using var file = new FileStream(Path.Combine(directory, $"{sample.FetchedAt:yyyy-MM}.jsonl"),
                    FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(bytes);
            }
        }
        catch (Exception) { /* History is best-effort and never changes live poll state. */ }
    }

    public History Read(StatsWindow window)
    {
        string? directory;
        try { directory = AccountDirectory(_accountHash()); }
        catch (Exception) { return new([], null); }
        if (directory is null) return new([], null);
        var samples = MonthlyUsageFiles.Between(directory, window.From, window.To)
            .SelectMany(ReadFile).Where(s => s.FetchedAt >= window.From && s.FetchedAt <= window.To)
            .GroupBy(s => s.FetchedAt).Select(group => group.Last())
            .OrderBy(s => s.FetchedAt).ToArray();
        DateTimeOffset? earliest = null;
        foreach (var path in MonthlyUsageFiles.All(directory))
        {
            earliest = ReadFile(path).Where(s => s.Windows.Count > 0)
                .Select(s => (DateTimeOffset?)s.FetchedAt).Min();
            if (earliest is not null) break;
        }
        return new(samples, earliest);
    }

    private static IEnumerable<Sample> ReadFile(string path)
    {
        foreach (var line in MonthlyUsageFiles.Lines(path))
        {
            Sample? sample;
            try { sample = JsonSerializer.Deserialize<Sample>(line, JsonOptions); }
            catch (JsonException) { continue; }
            if (sample?.Windows is null || sample.FetchedAt == default) continue;
            yield return sample with { Windows = UnambiguousWindows(sample.Windows) };
        }
    }
}
