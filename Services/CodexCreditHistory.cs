using System.Globalization;
using System.Text.Json;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Balance changes are observations, not an itemized billing ledger. Overnight offline periods
// are estimated between known balances; top-ups, missing balances and long gaps remain unknown.
public sealed class CodexCreditHistory
{
    private static readonly TimeSpan MaxGap = TimeSpan.FromHours(36);
    private static readonly TimeSpan MaxReadingAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BoundaryTolerance = TimeSpan.FromMinutes(3);
    private const int MaxSamples = 5000;
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private HistoryFile _history = new(1, null, []);
    private bool _loaded;
    private bool _persisted;

    public sealed record Sample(DateTimeOffset At, double? Balance);
    public sealed record HistoryFile(int Version, string? AccountHash, List<Sample> Samples);

    public CodexCreditHistory() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsage", "usage-log", "codex-credits.json")) { }

    public CodexCreditHistory(string path, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public CodexUsageData Record(CodexUsageData data, string? accountHash)
    {
        lock (_gate)
        {
            var now = _clock();
            Load(now);
            if (string.IsNullOrWhiteSpace(accountHash) || _history.AccountHash != accountHash)
                _history = new HistoryFile(1, accountHash, []);

            var valid = !data.IsStale && !data.CreditsUnlimited
                && data.CreditsBalance is { } balance && double.IsFinite(balance) && balance >= 0
                && data.FetchedAt <= now && now - data.FetchedAt <= MaxReadingAge;
            if (accountHash is null || string.IsNullOrWhiteSpace(accountHash)) valid = false;
            var at = valid ? data.FetchedAt : now;
            if (_history.Samples.Count > 0 && at <= _history.Samples[^1].At)
            {
                // A duplicate/out-of-order reading must not overwrite a newer balance or extend
                // a supposedly complete observation period.
                Invalidate(now);
                valid = false;
            }
            else
                _history.Samples.Add(new Sample(at, valid ? data.CreditsBalance : null));

            Prune(now);
            var saved = Save();
            var hasCreditData = data.CreditsBalance is not null || data.CreditsUnlimited
                || _history.Samples.Any(sample => sample.Balance is not null);
            var projected = hasCreditData ? Project(now, data.CreditsBalance, valid && saved) : null;
            if (!saved) _history = new HistoryFile(1, accountHash, []);
            return data with { CreditsHistory = projected };
        }
    }

    public void MarkUnavailable()
    {
        lock (_gate)
        {
            var now = _clock();
            Load(now);
            // A failed network poll supplies no new balance. Keep the previous endpoint so a
            // later fresh reading can estimate an ordinary overnight/sleep interval.
            Prune(now);
            if (!Save()) _history = new HistoryFile(1, _history.AccountHash, []);
        }
    }

    private void Invalidate(DateTimeOffset now)
    {
        if (_history.Samples.Count == 0 || now > _history.Samples[^1].At)
            _history.Samples.Add(new Sample(now, null));
        else
            _history.Samples[^1] = _history.Samples[^1] with { Balance = null };
    }

    private CodexCreditsHistory Project(DateTimeOffset now, double? balance, bool currentValid)
    {
        var daily = new List<CodexCreditDay>();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        for (var day = -7; day < 0; day++)
        {
            var start = today.AddDays(day);
            var pace = Calculate(start, start.AddDays(1), balance, false);
            daily.Add(new CodexCreditDay(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                pace.Status is "estimated" or "noSpend" ? pace.CreditsSpent : null));
        }
        var last24 = Calculate(now.AddDays(-1), now, balance, true);
        var last7 = Calculate(now.AddDays(-7), now, balance, true);
        if (!currentValid)
        {
            last24 = Uncertain(last24.ObservedHours);
            last7 = Uncertain(last7.ObservedHours);
        }
        return new CodexCreditsHistory(last24, last7, daily);
    }

    private CodexCreditPace Calculate(DateTimeOffset start, DateTimeOffset end,
        double? balance, bool allowTolerance)
    {
        var samples = _history.Samples;
        var first = samples.FindLastIndex(s => s.At <= start);
        if (first < 0) first = samples.FindIndex(s => s.At > start);
        if (first < 0) return new CodexCreditPace("collecting", null, null, null, 0);
        var observedStart = samples[first].At > start ? samples[first].At : start;
        var observedEnd = samples[^1].At < end ? samples[^1].At : end;
        var hours = Math.Max(0, (observedEnd - observedStart).TotalHours);
        var tolerance = allowTolerance ? BoundaryTolerance : TimeSpan.Zero;
        var complete = samples[first].At <= start + tolerance && samples[^1].At >= end - tolerance;
        var uncertain = false;
        var spent = 0d;
        for (var i = first; i < samples.Count - 1 && samples[i].At < end; i++)
        {
            var left = samples[i];
            var right = samples[i + 1];
            if (right.At <= start) continue;
            if (left.Balance is not { } before || right.Balance is not { } after
                || right.At - left.At > MaxGap || after > before)
            {
                uncertain = true;
                continue;
            }
            var intervalStart = left.At > start ? left.At : start;
            var intervalEnd = right.At < end ? right.At : end;
            // Allocate boundary-straddling changes proportionally to the observed interval.
            spent += (before - after) * (intervalEnd - intervalStart).TotalSeconds
                / (right.At - left.At).TotalSeconds;
        }
        if (uncertain || samples[first].Balance is null) return Uncertain(hours);
        if (!complete || hours <= 0) return new CodexCreditPace("collecting", null, null, null, hours);
        if (spent == 0) return new CodexCreditPace("noSpend", 0, 0, null, hours);
        var perDay = spent / (hours / 24);
        var daysLeft = balance / perDay;
        if (!double.IsFinite(perDay) || daysLeft is { } days && !double.IsFinite(days))
            return Uncertain(hours);
        return new CodexCreditPace("estimated", spent, perDay, daysLeft, hours);
    }

    private static CodexCreditPace Uncertain(double hours) => new("uncertain", null, null, null, hours);

    private void Prune(DateTimeOffset now)
    {
        _history.Samples.RemoveAll(s => s.At < now.AddDays(-8));
        if (_history.Samples.Count > MaxSamples)
            _history.Samples.RemoveRange(0, _history.Samples.Count - MaxSamples);
    }

    private void Load(DateTimeOffset now)
    {
        // Respect the existing clear-history action, including while this singleton is running.
        if (_loaded && (!_persisted || File.Exists(_path))) return;
        _loaded = true;
        _persisted = false;
        _history = new HistoryFile(1, null, []);
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 1024 * 1024) return;
            var stored = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(_path));
            if (stored is null || stored.Version != 1 || stored.Samples is null
                || stored.Samples.Count > MaxSamples) return;
            DateTimeOffset? previous = null;
            foreach (var sample in stored.Samples)
            {
                if (sample is null || sample.At > now || previous is { } prior && sample.At <= prior
                    || sample.Balance is { } value && (!double.IsFinite(value) || value < 0)) return;
                previous = sample.At;
            }
            _history = stored;
            _persisted = true;
            Prune(now);
        }
        catch { /* Corrupt/locked history never prevents a fresh usage reading. */ }
    }

    private bool Save()
    {
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_history));
            File.Move(temporary, _path, true);
            _persisted = true;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
