using System.Text.Json;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

public sealed class CodexCreditHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private const string Account = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codex-credit-tests-" + Guid.NewGuid());
    private string HistoryPath => Path.Combine(_directory, "history.json");

    private CodexUsageData Reading(double? balance, DateTimeOffset? at = null)
        => new([], balance, at ?? Now);

    private void Seed(int days, Action<List<CodexCreditHistory.Sample>>? edit = null,
        double dailySpend = 24, DateTimeOffset? at = null)
    {
        var end = at ?? Now;
        var samples = new List<CodexCreditHistory.Sample>();
        for (var i = 0; i < days * 96; i++)
            samples.Add(new(end.AddDays(-days).AddMinutes(i * 15), 1000 + days * dailySpend - i * dailySpend / 96));
        edit?.Invoke(samples);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(new CodexCreditHistory.HistoryFile(1, Account, samples)));
    }

    [Fact]
    public void CompleteWindows_UseMeasuredSpendAndSurviveReload()
    {
        Seed(8);
        var history = new CodexCreditHistory(HistoryPath, () => Now);
        var result = history.Record(Reading(1000), Account).CreditsHistory!;
        Assert.Equal("estimated", result.Last24Hours.Status);
        Assert.Equal(24, result.Last24Hours.CreditsSpent);
        Assert.Equal(24, result.Last24Hours.CreditsPerDay);
        Assert.Equal(1000d / 24, result.Last24Hours.EstimatedDaysLeft);
        Assert.Equal(168, result.Last7Days.CreditsSpent);
        Assert.Equal(168, result.Last7Days.ObservedHours);
        Assert.Equal(7, result.Daily.Count);
        Assert.All(result.Daily.Take(6), day => Assert.Equal(24, day.CreditsSpent));
        Assert.Equal(12, result.Daily[^1].CreditsSpent);
        Assert.Equal("2026-10-03", result.Daily[0].Date);
        Assert.Equal("2026-10-09", result.Daily[^1].Date);

        var later = Now.AddMinutes(15);
        var reloaded = new CodexCreditHistory(HistoryPath, () => later);
        var after = reloaded.Record(Reading(999.75, later), Account).CreditsHistory!;
        Assert.Equal(24, after.Last24Hours.CreditsSpent);
        Assert.True(new FileInfo(HistoryPath).Length < 1024 * 1024);
    }

    [Fact]
    public void NewHistory_DoesNotExtrapolateAPartialDay()
    {
        var clock = Now;
        var history = new CodexCreditHistory(HistoryPath, () => clock);
        history.Record(Reading(100), Account);
        clock = clock.AddMinutes(3);
        var result = history.Record(Reading(90, clock), Account).CreditsHistory!;
        Assert.Equal("collecting", result.Last24Hours.Status);
        Assert.Null(result.Last24Hours.EstimatedDaysLeft);
        Assert.All(result.Daily, day => Assert.Null(day.CreditsSpent));
    }

    [Fact]
    public void TodayShowsOnlySpendSoFar_AndUpdatesWithTheNextReading()
    {
        Seed(8);
        var clock = Now;
        var history = new CodexCreditHistory(HistoryPath, () => clock);
        var first = history.Record(Reading(1000), Account).CreditsHistory!;
        Assert.Equal(12, first.Daily[^1].CreditsSpent);
        clock = clock.AddHours(3);
        var later = history.Record(Reading(997, clock), Account).CreditsHistory!;
        Assert.Equal(15, later.Daily[^1].CreditsSpent);
        Assert.Equal(24, later.Last24Hours.CreditsSpent);
        Assert.Equal(168, later.Last7Days.CreditsSpent);
    }

    [Fact]
    public void TodayStopsAtTheFreshReading_NotTheLaterProcessingClock()
    {
        Seed(8);
        var result = new CodexCreditHistory(HistoryPath, () => Now.AddSeconds(1))
            .Record(Reading(1000), Account).CreditsHistory!;
        Assert.Equal(12, result.Daily[^1].CreditsSpent);
        Assert.Equal(24, result.Last24Hours.CreditsPerDay!.Value, 3);
    }

    [Theory]
    [InlineData("2026-10-28T16:00:00Z", "2026-10-22", "2026-10-28", 16)]
    [InlineData("2026-11-01T12:00:00Z", "2026-10-26", "2026-11-01", 12)]
    [InlineData("2027-01-01T12:00:00Z", "2026-12-26", "2027-01-01", 12)]
    [InlineData("2028-03-01T12:00:00Z", "2028-02-24", "2028-03-01", 12)]
    [InlineData("2026-10-28T00:30:00+01:00", "2026-10-21", "2026-10-27", 23.5)]
    public void DailyWindowIncludesTodayAndSixPreviousUtcDates(string at, string first, string last, double todaySpend)
    {
        var clock = DateTimeOffset.Parse(at);
        Seed(8, at: clock);
        var result = new CodexCreditHistory(HistoryPath, () => clock)
            .Record(Reading(1000, clock), Account).CreditsHistory!;
        Assert.Equal(7, result.Daily.Count);
        Assert.Equal(first, result.Daily[0].Date);
        Assert.Equal(last, result.Daily[^1].Date);
        Assert.Equal(todaySpend, result.Daily[^1].CreditsSpent);
        Assert.All(result.Daily.Take(6), day => Assert.Equal(24, day.CreditsSpent));
    }

    [Fact]
    public void MidnightStartsAnUnknownTodayRatherThanInventingZeroSpend()
    {
        Seed(8);
        var clock = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var result = new CodexCreditHistory(HistoryPath, () => clock)
            .Record(Reading(988, clock), Account).CreditsHistory!;
        Assert.Equal("2026-10-04", result.Daily[0].Date);
        Assert.Equal("2026-10-10", result.Daily[^1].Date);
        Assert.Null(result.Daily[^1].CreditsSpent);
        Assert.Equal(24, result.Daily[^2].CreditsSpent);
    }

    [Theory]
    [InlineData("topup")]
    [InlineData("gap")]
    [InlineData("missing")]
    public void AmbiguousIntervals_SuppressAffectedForecasts(string fault)
    {
        Seed(8, samples =>
        {
            if (fault == "gap") samples.RemoveRange(samples.Count - 160, 159);
            else samples[^2] = samples[^2] with { Balance = fault == "missing" ? null : 2000 };
        });
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(Reading(1000), Account).CreditsHistory!;
        Assert.Equal("uncertain", result.Last24Hours.Status);
        Assert.Equal("uncertain", result.Last7Days.Status);
        Assert.Null(result.Last24Hours.CreditsPerDay);
        Assert.Null(result.Last7Days.EstimatedDaysLeft);
    }

    [Fact]
    public void OvernightGap_CanEstimateFromKnownBalances()
    {
        Seed(8, samples => samples.RemoveRange(samples.Count - 33, 32));
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(Reading(1000), Account).CreditsHistory!;
        Assert.Equal("estimated", result.Last24Hours.Status);
        Assert.Equal("estimated", result.Last7Days.Status);
        Assert.Equal(24, result.Last24Hours.CreditsSpent);
    }

    [Fact]
    public void NoSpend_HasNoInfinitePrediction()
    {
        Seed(8, dailySpend: 0);
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(Reading(1000), Account).CreditsHistory!;
        Assert.Equal("noSpend", result.Last24Hours.Status);
        Assert.Equal(0, result.Last24Hours.CreditsPerDay);
        Assert.Null(result.Last24Hours.EstimatedDaysLeft);
    }

    [Fact]
    public void ExhaustedBalance_ProducesZeroDays()
    {
        Seed(8);
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(Reading(0), Account).CreditsHistory!;
        Assert.Equal("estimated", result.Last24Hours.Status);
        Assert.Equal(0, result.Last24Hours.EstimatedDaysLeft);
    }

    [Theory]
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [InlineData(null)]
    public void DifferentOrUnknownAccount_CannotReuseHistory(string? account)
    {
        Seed(8);
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(Reading(1000), account).CreditsHistory!;
        Assert.Null(result.Last24Hours.EstimatedDaysLeft);
        Assert.Equal(0, result.Last24Hours.ObservedHours);
        Assert.All(result.Daily, day => Assert.Null(day.CreditsSpent));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("old")]
    [InlineData("missing")]
    [InlineData("unlimited")]
    public void InvalidCurrentReading_CannotProduceForecast(string fault)
    {
        Seed(8);
        var data = fault switch
        {
            "stale" => Reading(1000) with { IsStale = true },
            "future" => Reading(1000, Now.AddMinutes(1)),
            "old" => Reading(1000, Now.AddHours(-1)),
            "unlimited" => Reading(1000) with { CreditsUnlimited = true },
            _ => Reading(null)
        };
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(data, Account).CreditsHistory!;
        Assert.Equal("uncertain", result.Last24Hours.Status);
        Assert.Null(result.Last24Hours.EstimatedDaysLeft);
        Assert.Null(result.Daily[^1].CreditsSpent);
    }

    [Fact]
    public void MissedNetworkPoll_PreservesEndpointsButDuplicateInvalidates()
    {
        Seed(8);
        var clock = Now;
        var history = new CodexCreditHistory(HistoryPath, () => clock);
        history.MarkUnavailable();
        clock = clock.AddMinutes(3);
        var result = history.Record(Reading(1000, clock), Account).CreditsHistory!;
        Assert.Equal("estimated", result.Last24Hours.Status);
        result = history.Record(Reading(1000, clock), Account).CreditsHistory!;
        Assert.Equal("uncertain", result.Last24Hours.Status);
    }

    [Fact]
    public void CorruptFile_RecoversWithoutInventingHistory()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(HistoryPath, "{broken");
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(Reading(1000), Account).CreditsHistory!;
        Assert.Equal("collecting", result.Last24Hours.Status);
        Assert.NotNull(JsonSerializer.Deserialize<CodexCreditHistory.HistoryFile>(File.ReadAllText(HistoryPath)));
    }

    [Fact]
    public void DeletedHistory_IsAlsoClearedFromMemory()
    {
        Seed(8);
        var clock = Now;
        var history = new CodexCreditHistory(HistoryPath, () => clock);
        history.Record(Reading(1000), Account);
        File.Delete(HistoryPath);
        clock = clock.AddMinutes(3);
        var result = history.Record(Reading(999, clock), Account).CreditsHistory!;
        Assert.Equal("collecting", result.Last24Hours.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Account)]
    public void NeverKnownBalance_DoesNotSurfaceEmptyCreditHistory(string? account)
    {
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(Reading(null), account);
        Assert.Null(result.CreditsHistory);
    }

    [Fact]
    public void MissingBalance_PreservesKnownAccountHistory()
    {
        Seed(8);
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(Reading(null), Account);
        Assert.NotNull(result.CreditsHistory);
        Assert.Equal("uncertain", result.CreditsHistory.Last24Hours.Status);
        Assert.Contains(result.CreditsHistory.Daily, day => day.CreditsSpent is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    public void AccountChangeWithoutBalance_DoesNotSurfacePreviousAccountHistory(string? account)
    {
        Seed(8);
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(Reading(null), account);
        Assert.Null(result.CreditsHistory);
    }

    [Fact]
    public void UnlimitedWithoutBalance_StillSurfacesCreditHistory()
    {
        var data = Reading(null) with { CreditsUnlimited = true };
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(data, Account);
        Assert.NotNull(result.CreditsHistory);
        Assert.Null(result.CreditsHistory.Last24Hours.EstimatedDaysLeft);
    }

    [Fact]
    public void PersistenceFailure_DoesNotClaimAUsableForecast()
    {
        Directory.CreateDirectory(_directory);
        // A directory at the file path causes atomic replacement to fail on every platform.
        Directory.CreateDirectory(HistoryPath);
        var result = new CodexCreditHistory(HistoryPath, () => Now).Record(Reading(1000), Account).CreditsHistory!;
        Assert.Equal("uncertain", result.Last24Hours.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
