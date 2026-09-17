using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

// Covers AgentSpendFreshnessPolicy: the pure "adopt this read, or retain the last-good snapshot"
// and "is an adopted read still fresh" rules AgentSpendPoller relies on.
public class AgentSpendFreshnessPolicyTests
{
    [Fact]
    public void ShouldAdopt_TrueOnlyForExactlyOkStatus()
    {
        Assert.True(AgentSpendFreshnessPolicy.ShouldAdopt("ok"));
        Assert.False(AgentSpendFreshnessPolicy.ShouldAdopt("degraded"));
        Assert.False(AgentSpendFreshnessPolicy.ShouldAdopt("error"));
        Assert.False(AgentSpendFreshnessPolicy.ShouldAdopt(""));
        Assert.False(AgentSpendFreshnessPolicy.ShouldAdopt("OK"));   // case-sensitive: the contract value is "ok"
    }

    [Fact]
    public void IsFreshEnough_TrueWithinWindow_FalseBeyondIt()
    {
        var now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

        Assert.True(AgentSpendFreshnessPolicy.IsFreshEnough(now, now));
        Assert.True(AgentSpendFreshnessPolicy.IsFreshEnough(now - AgentSpendFreshnessPolicy.FreshWindow, now));
        Assert.False(AgentSpendFreshnessPolicy.IsFreshEnough(now - AgentSpendFreshnessPolicy.FreshWindow - TimeSpan.FromSeconds(1), now));
    }

    [Fact]
    public void IsFreshEnough_FutureFileTimestamp_StillFresh()
    {
        // Filesystem timestamp skew should not manufacture a false "stale".
        var now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        Assert.True(AgentSpendFreshnessPolicy.IsFreshEnough(now.AddMinutes(5), now));
    }
}
