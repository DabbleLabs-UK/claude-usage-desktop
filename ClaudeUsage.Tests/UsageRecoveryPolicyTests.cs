using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

public class UsageRecoveryPolicyTests
{
    [Fact]
    public void SuccessfulPoll_ClearsSignedOutState_WhenFailureCounterWasNotIncremented()
    {
        var signedOut = new BackoffInfo(
            ConnectivityState.SignedOut,
            ConsecutiveFailures: 0,
            IntervalSeconds: 180,
            NextAttemptAt: DateTimeOffset.UtcNow.AddMinutes(3));

        Assert.True(UsageRecoveryPolicy.ShouldClearBackoff(0, signedOut));
    }

    [Fact]
    public void SuccessfulPoll_WithNoFailureOrBanner_NeedsNoReset()
    {
        Assert.False(UsageRecoveryPolicy.ShouldClearBackoff(0, null));
    }
}
