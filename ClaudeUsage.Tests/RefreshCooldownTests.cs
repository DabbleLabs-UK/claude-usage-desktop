using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

// Covers the refresh-cooldown duration policy that stops the app from re-arming the token
// endpoint's rate limit every poll (the self-inflicted 429 lockout).
public class RefreshCooldownTests
{
    private const long Min15 = 15L * 60 * 1000;
    private const long Min30 = 30L * 60 * 1000;
    private const long Min60 = 60L * 60 * 1000;

    [Theory]
    [InlineData(1, Min15)] // first 429 -> base
    [InlineData(2, Min30)] // doubles
    [InlineData(3, Min60)] // doubles to the cap
    [InlineData(4, Min60)] // held at the cap
    [InlineData(10, Min60)]
    public void ExponentialRamp_DoublesPerConsecutive429_CappedAtMax(int consecutive, long expected)
    {
        Assert.Equal(expected, RefreshCooldownPolicy.ComputeMs(consecutive, retryAfterMs: null));
    }

    [Fact]
    public void ZeroOrNegativeConsecutive_TreatedAsFirstAttempt()
    {
        Assert.Equal(Min15, RefreshCooldownPolicy.ComputeMs(0, null));
        Assert.Equal(Min15, RefreshCooldownPolicy.ComputeMs(-3, null));
    }

    [Fact]
    public void RetryAfter_TakesPrecedenceOverExponentialRamp()
    {
        // Even at a high consecutive count, an explicit Retry-After wins (here, shorter).
        Assert.Equal(5000, RefreshCooldownPolicy.ComputeMs(5, retryAfterMs: 5000));
        // ...and longer than the cap is honoured too -- the server's wait is authoritative.
        Assert.Equal(Min60 * 3, RefreshCooldownPolicy.ComputeMs(1, retryAfterMs: Min60 * 3));
    }

    [Fact]
    public void NegativeRetryAfter_ClampsToZero()
    {
        Assert.Equal(0, RefreshCooldownPolicy.ComputeMs(1, retryAfterMs: -1000));
    }

    [Fact]
    public void CooldownConstants_AreFifteenAndSixtyMinutes()
    {
        Assert.Equal(Min15, RefreshCooldownPolicy.BaseMs);
        Assert.Equal(Min60, RefreshCooldownPolicy.MaxMs);
    }
}
