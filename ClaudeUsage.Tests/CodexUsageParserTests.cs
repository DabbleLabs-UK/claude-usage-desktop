using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

// Covers CodexUsageParser: the generic, lane-agnostic parse of the Codex usage response
// (GET /backend-api/wham/usage), plus the untracked-window filtering it performs at the source
// (a null / unpopulated lane -- e.g. secondary_window on a Plus plan -- must produce NO window,
// never an empty placeholder). Nothing is hardcoded to "primary"/"secondary": any sub-object of
// rate_limit carrying used_percent + limit_window_seconds is a window, keyed by its window length.
public class CodexUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    // 2026-07-30T12:00:00Z as unix seconds (one week after Now) -- used as a concrete reset_at.
    private const long WeekResetUnix = 1785585600L;

    [Fact]
    public void PlusPlan_OnlyPrimaryPopulated_SecondaryNull_YieldsExactlyOneWindow()
    {
        // Plus plan: primary weekly window populated, secondary_window explicitly null.
        var body = $$"""
        {
          "rate_limit": {
            "primary_window":   { "used_percent": 42.5, "limit_window_seconds": 604800, "reset_after_seconds": 604800, "reset_at": {{WeekResetUnix}} },
            "secondary_window": null
          },
          "credits": { "has_credits": false }
        }
        """;

        var result = CodexUsageParser.Parse(body, Now);

        // The null lane is filtered out -- only the populated weekly window survives.
        Assert.Single(result.Windows);
        var w = result.Windows[0];
        Assert.Equal(42.5, w.UsedPercent);
        Assert.Equal(604800, w.LimitWindowSeconds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(WeekResetUnix), w.ResetAt);
        Assert.Null(result.CreditsBalance);
    }

    [Fact]
    public void BothWindowsPopulated_YieldsTwoWindows_SortedByWindowLengthAscending()
    {
        // Deliberately list the longer (weekly) lane FIRST to prove ordering is by window length,
        // not response order: the 5-hour session window must come out first.
        var body = """
        {
          "rate_limit": {
            "primary_window":   { "used_percent": 10, "limit_window_seconds": 604800, "reset_after_seconds": 604800 },
            "secondary_window": { "used_percent": 80, "limit_window_seconds": 18000,  "reset_after_seconds": 3600 }
          }
        }
        """;

        var result = CodexUsageParser.Parse(body, Now);

        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(18000, result.Windows[0].LimitWindowSeconds);   // 5-hour session first
        Assert.Equal(604800, result.Windows[1].LimitWindowSeconds);  // weekly second
        Assert.Equal(80, result.Windows[0].UsedPercent);
    }

    [Fact]
    public void ResetAfterSeconds_UsedWhenResetAtAbsent()
    {
        var body = """
        {
          "rate_limit": {
            "primary_window": { "used_percent": 5, "limit_window_seconds": 604800, "reset_after_seconds": 3600 }
          }
        }
        """;

        var result = CodexUsageParser.Parse(body, Now);

        Assert.Single(result.Windows);
        Assert.Equal(Now.AddSeconds(3600), result.Windows[0].ResetAt);
    }

    [Fact]
    public void CreditsBalance_SurfacedOnlyWhenHasCreditsTrue()
    {
        var body = """
        {
          "rate_limit": { "primary_window": { "used_percent": 1, "limit_window_seconds": 604800 } },
          "credits": { "has_credits": true, "balance": 12.34 }
        }
        """;

        var result = CodexUsageParser.Parse(body, Now);

        Assert.Equal(12.34, result.CreditsBalance);
    }

    [Fact]
    public void CreditsBalance_NullWhenHasCreditsFalse_EvenIfBalancePresent()
    {
        var body = """
        {
          "rate_limit": { "primary_window": { "used_percent": 1, "limit_window_seconds": 604800 } },
          "credits": { "has_credits": false, "balance": 99.0 }
        }
        """;

        var result = CodexUsageParser.Parse(body, Now);

        Assert.Null(result.CreditsBalance);
    }

    [Fact]
    public void LaneMissingRequiredFields_IsFilteredOut()
    {
        // One lane lacks limit_window_seconds, another lacks used_percent -- both are "not a window"
        // and must be dropped, leaving only the fully-formed lane.
        var body = """
        {
          "rate_limit": {
            "primary_window":   { "used_percent": 20, "limit_window_seconds": 604800 },
            "no_window_seconds":{ "used_percent": 30 },
            "no_used_percent":  { "limit_window_seconds": 18000 }
          }
        }
        """;

        var result = CodexUsageParser.Parse(body, Now);

        Assert.Single(result.Windows);
        Assert.Equal(604800, result.Windows[0].LimitWindowSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{ "rate_limit": null }""")]
    [InlineData("""{ "rate_limit": { "primary_window": null, "secondary_window": null } }""")]
    public void MalformedOrEmptyOrAllNull_YieldsEmptyWindows_NoThrow(string body)
    {
        var result = CodexUsageParser.Parse(body, Now);

        Assert.Empty(result.Windows);
        Assert.Null(result.CreditsBalance);
        Assert.Equal(Now, result.FetchedAt);
    }
}
