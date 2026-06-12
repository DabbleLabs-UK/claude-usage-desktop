using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

// Covers the refresh-cooldown duration policy that stops the app from re-arming the token
// endpoint's STICKY, no-Retry-After rate limit every poll (the self-inflicted 429 lockout).
// The token endpoint 429s before validating the token and re-extends the block on every hit,
// so a no-Retry-After 429 maps to a multi-hour cooldown; Retry-After, when present, wins.
public class RefreshCooldownTests
{
    private const long Hour4  = 4L * 60 * 60 * 1000;
    private const long Hour8  = 8L * 60 * 60 * 1000;
    private const long Hour12 = 12L * 60 * 60 * 1000;
    private const long Min60  = 60L * 60 * 1000;

    [Theory]
    [InlineData(1, Hour4)]  // first no-Retry-After 429 -> sticky base
    [InlineData(2, Hour8)]  // doubles
    [InlineData(3, Hour12)] // doubles to the cap
    [InlineData(4, Hour12)] // held at the cap
    [InlineData(10, Hour12)]
    public void StickyRamp_DoublesPerConsecutive429_CappedAtMax(int consecutive, long expected)
    {
        Assert.Equal(expected, RefreshCooldownPolicy.ComputeMs(consecutive, retryAfterMs: null));
    }

    [Fact]
    public void ZeroOrNegativeConsecutive_TreatedAsFirstAttempt()
    {
        Assert.Equal(Hour4, RefreshCooldownPolicy.ComputeMs(0, null));
        Assert.Equal(Hour4, RefreshCooldownPolicy.ComputeMs(-3, null));
    }

    [Fact]
    public void RetryAfter_TakesPrecedenceOverStickyRamp()
    {
        // Even at a high consecutive count, an explicit Retry-After wins (here, far shorter
        // than the sticky multi-hour floor).
        Assert.Equal(5000, RefreshCooldownPolicy.ComputeMs(5, retryAfterMs: 5000));
        // ...and a Retry-After longer than the sticky cap is honoured too -- the server's wait
        // is authoritative.
        Assert.Equal(Hour12 * 2, RefreshCooldownPolicy.ComputeMs(1, retryAfterMs: Hour12 * 2));
    }

    [Fact]
    public void NegativeRetryAfter_ClampsToZero()
    {
        Assert.Equal(0, RefreshCooldownPolicy.ComputeMs(1, retryAfterMs: -1000));
    }

    [Fact]
    public void StickyConstants_AreFourAndTwelveHours()
    {
        Assert.Equal(Hour4,  RefreshCooldownPolicy.StickyBaseMs);
        Assert.Equal(Hour12, RefreshCooldownPolicy.StickyMaxMs);
        // The sticky floor is deliberately far above the old 60-min cap that re-armed the box.
        Assert.True(RefreshCooldownPolicy.StickyBaseMs > Min60);
    }
}
