using System.Text.Json;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeUsage.Tests;

public sealed class StatsHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 15, 0, TimeSpan.Zero);
    private const string AccountA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string AccountB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "stats-history-" + Guid.NewGuid());
    private string? _account = AccountA;
    private CodexQuotaLog Codex() => new(_directory, () => _account);
    private UsageLog Claude() => new(NullLogger<UsageLog>.Instance, _directory);
    private static StatsWindow Window(int days = 1) => new(days + "d", Now.AddDays(-days), Now);
    private static CodexUsageData Reading(DateTimeOffset time, params CodexWindow[] windows) =>
        new(windows, 9876.5, time);
    private static CodexWindow Quota(double value, long seconds = 18000, DateTimeOffset? reset = null) =>
        new(value, seconds, reset ?? Now.AddHours(4));

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("1d", "2026-09-30T12:00:00Z", true)]
    [InlineData("7d", "2026-09-30T13:00:00+01:00", true)]
    [InlineData("30d", "2026-09-30T12:00:00.1234567Z", true)]
    [InlineData("2d", null, false)]
    [InlineData("", null, false)]
    [InlineData("1d", "", false)]
    [InlineData("1d", "2026-09-30", false)]
    [InlineData("1d", "2026-09-30T12:00:00", false)]
    [InlineData("1d", "2026-02-30T12:00:00Z", false)]
    [InlineData("30d", "0001-01-01T00:00:00Z", false)]
    public void RangeValidation(string? range, string? end, bool valid) =>
        Assert.Equal(valid, StatsWindow.TryResolve(range, end, Now, out _));

    [Fact]
    public void FutureClampsAndOffsetNormalizes()
    {
        Assert.True(StatsWindow.TryResolve("7d", Now.AddDays(1).ToString("O"), Now, out var future));
        Assert.Equal(Now, future.To);
        Assert.Equal(Now.AddDays(-7), future.From);
        Assert.True(StatsWindow.TryResolve(null, "2026-09-30T13:00:00+01:00", Now, out var past));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero), past.To);
        Assert.Equal(TimeSpan.Zero, past.To.Offset);
    }

    [Fact]
    public void CodexMonthlyPersistenceIsAccountSeparatedAndContainsOnlyQuotas()
    {
        var log = Codex();
        log.Append(Reading(Now.AddMinutes(-20), Quota(10)), AccountA);
        log.Append(Reading(Now, Quota(25), Quota(5, 604800)), AccountA);
        log.Append(Reading(Now, Quota(99)), AccountB);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_directory, "codex", AccountA)).Length);
        var json = File.ReadAllText(Path.Combine(_directory, "codex", AccountA, "2026-10.jsonl"));
        Assert.DoesNotContain("credit", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("9876", json);
        Assert.DoesNotContain(AccountA, json);
        Assert.Equal(2, Codex().Read(Window()).Samples.Count);
        _account = AccountB;
        Assert.Equal(99, Assert.Single(Assert.Single(Codex().Read(Window()).Samples).Windows).UsedPercent);
        _account = null;
        Assert.Empty(log.Read(Window()).Samples);
        Assert.Null(log.Read(Window()).Earliest);
        _account = "../";
        Assert.Empty(log.Read(Window()).Samples);
    }

    [Fact]
    public void OnlyFreshIdentifiedFiniteSnapshotsAreLoggedAndEmptyWindowsStayMissing()
    {
        var log = Codex();
        log.Append(Reading(Now, Quota(10)) with { IsStale = true }, AccountA);
        log.Append(Reading(Now, Quota(10)), null);
        log.Append(Reading(Now, Quota(10)), "../bad");
        Assert.False(Directory.Exists(_directory));
        log.Append(Reading(Now, Quota(double.NaN), Quota(double.PositiveInfinity), Quota(-1),
            Quota(101), Quota(10, 0)), AccountA);
        Assert.Empty(Assert.Single(log.Read(Window()).Samples).Windows);
        Assert.False(StatsHistory.Codex(log, Window()).HasData);
        Assert.Null(log.Read(Window()).Earliest);
    }

    [Fact]
    public void ConflictingDuplicateDurationsAreMissingRatherThanArbitrarilySelected()
    {
        var log = Codex();
        log.Append(Reading(Now.AddMinutes(-9), Quota(10), Quota(10)), AccountA);
        log.Append(Reading(Now.AddMinutes(-6), Quota(15), Quota(90)), AccountA);
        log.Append(Reading(Now.AddMinutes(-3), Quota(20)), AccountA);
        log.Append(Reading(Now, Quota(23)), AccountA);
        var points = Assert.Single(StatsHistory.Codex(log, Window()).Series).Points;
        Assert.Equal(new double[] { 0, 0, 3 }, points.Select(point => point.V));
        Assert.True(points[1].BreakBefore);
        Assert.Empty(log.Read(Window()).Samples[1].Windows);
    }

    [Fact]
    public void VariableDurationsUseObservedIncreasesAndRestartAtCycleChanges()
    {
        var log = Codex();
        var reset = Now.AddHours(1);
        log.Append(Reading(Now.AddMinutes(-12), Quota(10, 7200, reset), Quota(8, 604800)), AccountA);
        log.Append(Reading(Now.AddMinutes(-9), Quota(15, 7200, reset.AddSeconds(4))), AccountA);
        log.Append(Reading(Now.AddMinutes(-6), Quota(80, 7200, reset.AddHours(2)), Quota(20, 604800)), AccountA);
        log.Append(Reading(Now.AddMinutes(-3), Quota(85, 7200, reset.AddHours(2)), Quota(21, 604800)), AccountA);
        log.Append(Reading(Now, Quota(2, 7200, reset.AddHours(2)), Quota(22, 604800)), AccountA);
        var result = StatsHistory.Codex(log, Window());
        Assert.True(result.HasData);
        var shortWindow = result.Series.Single(s => s.Id == "duration-7200");
        Assert.Equal("2-hour", shortWindow.Label);
        Assert.Equal(new double[] { 0, 5, 5, 10, 10 }, shortWindow.Points.Select(p => p.V));
        Assert.True(shortWindow.Points[2].BreakBefore);
        var week = result.Series.Single(s => s.Id == "duration-604800");
        Assert.Equal(new double[] { 0, 0, 1, 2 }, week.Points.Select(p => p.V));
        Assert.True(week.Points[1].BreakBefore);
    }

    [Fact]
    public void LongGapsAndKnownResetCrossingsDoNotInventConsumption()
    {
        var log = Codex();
        var reset = Now.AddMinutes(-1);
        log.Append(Reading(Now.AddMinutes(-30), Quota(5, reset: reset)), AccountA);
        log.Append(Reading(Now.AddMinutes(-3), Quota(45, reset: reset)), AccountA);
        log.Append(Reading(Now, Quota(80, reset: reset)), AccountA);
        var points = Assert.Single(StatsHistory.Codex(log, Window()).Series).Points;
        Assert.Equal(new double[] { 0, 0, 0 }, points.Select(p => p.V));
        Assert.All(points.Skip(1), point => Assert.True(point.BreakBefore));
    }

    [Fact]
    public void BoundariesAreInclusiveWithNoOutsideBaselineAndDuplicatesAreDeterministic()
    {
        var log = Codex();
        var window = Window();
        log.Append(Reading(window.From.AddSeconds(-1), Quota(1)), AccountA);
        log.Append(Reading(window.To, Quota(30)), AccountA);
        log.Append(Reading(window.From, Quota(10)), AccountA);
        log.Append(Reading(window.From, Quota(12)), AccountA);
        log.Append(Reading(window.To.AddSeconds(1), Quota(99)), AccountA);
        var history = log.Read(window);
        Assert.Equal(new[] { window.From, window.To }, history.Samples.Select(s => s.FetchedAt));
        Assert.Equal(12, history.Samples[0].Windows[0].UsedPercent);
        Assert.Equal(window.From.AddSeconds(-1), history.Earliest);
        var narrow = new StatsWindow("1d", window.From.AddSeconds(1), window.To.AddSeconds(-1));
        Assert.False(StatsHistory.Codex(log, narrow).HasData);
    }

    [Fact]
    public void TornLinesLockedMonthsAndClearHistoryAreTolerated()
    {
        var log = Codex();
        log.Append(Reading(Now.AddMinutes(-20), Quota(5)), AccountA);
        log.Append(Reading(Now, Quota(10)), AccountA);
        var october = Path.Combine(_directory, "codex", AccountA, "2026-10.jsonl");
        File.AppendAllText(october, "{torn\n{\"fetchedAt\":\"2026-10-01T00:15:00Z\",\"windows\":null}\n");
        Assert.Equal(2, log.Read(Window()).Samples.Count);
        using (new FileStream(october, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Single(log.Read(Window()).Samples);
        Directory.Delete(_directory, true);
        Assert.Empty(log.Read(Window()).Samples);
        Assert.Null(log.Read(Window()).Earliest);
        log.Append(Reading(Now, Quota(20)), AccountA);
        Assert.Single(Codex().Read(Window()).Samples);
    }

    [Fact]
    public void LoggingFailureDoesNotEscape()
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "file");
        File.WriteAllText(file, "not a directory");
        var log = new CodexQuotaLog(file, () => AccountA);
        log.Append(Reading(Now, Quota(10)), AccountA);
        Assert.Empty(log.Read(Window()).Samples);
    }

    [Fact]
    public void ClaudeContractPreservesAliasesAndUsesActualPlottedData()
    {
        var log = Claude();
        log.Append(new(new Dictionary<string, UsageWindow?> { ["seven_day_opus"] = new(40, null) }, null, Now));
        Assert.False(StatsHistory.Claude(log, Window()).HasData);
        Assert.Null(log.Earliest());
        log.Append(new(new Dictionary<string, UsageWindow?> { ["five_hour"] = new(10, null) }, null, Now.AddMinutes(-3)));
        log.Append(new(new Dictionary<string, UsageWindow?> { ["five_hour"] = new(15, null) }, null, Now));
        var result = StatsHistory.Claude(log, Window());
        Assert.True(result.HasData);
        Assert.Equal(new double[] { 0, 5 }, result.Session!.Select(p => p.V));
        Assert.Same(result.Session, result.Series[0].Points);
        Assert.Empty(result.Week!);
        Assert.Equal(Now.AddMinutes(-3), result.Earliest);
        Assert.Equal(Window().From, result.From);
        Assert.Equal(Window().To, result.To);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "earliest", "from", "hasData", "range", "series", "session", "to", "week" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("session", doc.RootElement.GetProperty("series")[0].GetProperty("id").GetString());
        Directory.Delete(_directory, true);
        Assert.Null(log.Earliest());
        Assert.False(StatsHistory.Claude(log, Window()).HasData);
    }

    [Fact]
    public void CodexWireContractDoesNotExposeAccountIdentityOrCreditBalance()
    {
        var log = Codex();
        log.Append(Reading(Now.AddMinutes(-20), Quota(10, 90)), AccountA);
        log.Append(Reading(Now, Quota(20, 90)), AccountA);
        var json = JsonSerializer.Serialize(StatsHistory.Codex(log, Window()),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "earliest", "from", "hasData", "range", "series", "to" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("90-second", doc.RootElement.GetProperty("series")[0].GetProperty("label").GetString());
        Assert.True(doc.RootElement.GetProperty("series")[0].GetProperty("points")[1].GetProperty("breakBefore").GetBoolean());
        Assert.DoesNotContain(AccountA, json);
        Assert.DoesNotContain("credit", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClaudeRejectsNonFiniteReadingsAndReadsAcrossMonths()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "2026-09.jsonl"),
            "{\"fetchedAt\":\"2026-09-30T23:59:00Z\",\"windows\":{\"five_hour\":{\"utilization\":1e999}}}\n{torn\n");
        var log = Claude();
        log.Append(new(new Dictionary<string, UsageWindow?> { ["five_hour"] = new(5, null) }, null, Now));
        Assert.Single(StatsHistory.Claude(log, Window()).Session!);
        Assert.Equal(Now, log.Earliest());
    }

    [Fact]
    public void ClaudeLastSampleSurvivesTornFinalLineAndOutOfOrderRecords()
    {
        var log = Claude();
        var windows = new Dictionary<string, UsageWindow?> { ["five_hour"] = new(5, null) };
        log.Append(new(windows, null, Now));
        log.Append(new(windows, null, Now.AddMinutes(-3)));
        File.AppendAllText(Path.Combine(_directory, "2026-10.jsonl"), "{torn\n");
        Assert.Equal(Now, log.ReadLastSample()!.FetchedAt);
    }

    [Fact]
    public void AppendAfterATornFinalLinePreservesTheNextRecord()
    {
        var codex = Codex();
        codex.Append(Reading(Now.AddMinutes(-3), Quota(5)), AccountA);
        File.AppendAllText(Path.Combine(_directory, "codex", AccountA, "2026-10.jsonl"), "{torn");
        codex.Append(Reading(Now, Quota(8)), AccountA);
        Assert.Equal(2, codex.Read(Window()).Samples.Count);

        var claude = Claude();
        var windows = new Dictionary<string, UsageWindow?> { ["five_hour"] = new(5, null) };
        claude.Append(new(windows, null, Now.AddMinutes(-3)));
        File.AppendAllText(Path.Combine(_directory, "2026-10.jsonl"), "{torn");
        claude.Append(new(windows, null, Now));
        Assert.Equal(2, claude.ReadRange(Window().From, Window().To).Count);
        Assert.Equal(Now, claude.ReadLastSample()!.FetchedAt);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
