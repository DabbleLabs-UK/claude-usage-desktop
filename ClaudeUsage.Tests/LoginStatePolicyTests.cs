using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

// Covers LoginStatePolicy.Classify -- the fix for the "dead login" bug where expiresAt==0 with a
// refresh token present fell through the "unknown expiry, don't guess" guard meant for static
// setup-tokens, was never refreshed, and eventually looped into an endless "waiting on Claude
// Code to refresh" banner with nothing ever actually coming.
public class LoginStatePolicyTests
{
    private const long Now = 1_800_000_000_000; // arbitrary fixed instant (unix ms)
    private const long OneHour = 60 * 60 * 1000;

    [Fact]
    public void NoRefreshToken_IsStatic_RegardlessOfExpiresAt()
    {
        Assert.Equal(LoginState.Static, LoginStatePolicy.Classify(
            hasRefreshToken: false, expiresAtMs: 0, refreshTokenExpiresAtMs: 0, nowMs: Now));

        // Even a populated (but meaningless, for a static token) expiresAt doesn't change this.
        Assert.Equal(LoginState.Static, LoginStatePolicy.Classify(
            hasRefreshToken: false, expiresAtMs: Now + OneHour, refreshTokenExpiresAtMs: 0, nowMs: Now));
    }

    [Fact]
    public void RefreshTokenPresent_ExpiresAtZero_IsDead()
    {
        // The exact real-world signature: CC writes literal 0 on a lapsed login, refresh token
        // still present on disk.
        Assert.Equal(LoginState.Dead, LoginStatePolicy.Classify(
            hasRefreshToken: true, expiresAtMs: 0, refreshTokenExpiresAtMs: 0, nowMs: Now));
    }

    [Fact]
    public void RefreshTokenPresent_ExpiresAtNegative_IsDead()
    {
        Assert.Equal(LoginState.Dead, LoginStatePolicy.Classify(
            hasRefreshToken: true, expiresAtMs: -1, refreshTokenExpiresAtMs: 0, nowMs: Now));
    }

    [Fact]
    public void RefreshTokenExpiresAt_InThePast_IsDead_EvenWithLiveExpiresAt()
    {
        // expiresAt still looks live, but the refresh token itself has lapsed -- still dead,
        // since nothing can renew the access token once it does expire.
        Assert.Equal(LoginState.Dead, LoginStatePolicy.Classify(
            hasRefreshToken: true, expiresAtMs: Now + OneHour, refreshTokenExpiresAtMs: Now - 1, nowMs: Now));
    }

    [Fact]
    public void RefreshTokenExpiresAt_ExactlyNow_IsDead()
    {
        Assert.Equal(LoginState.Dead, LoginStatePolicy.Classify(
            hasRefreshToken: true, expiresAtMs: Now + OneHour, refreshTokenExpiresAtMs: Now, nowMs: Now));
    }

    [Fact]
    public void HealthyToken_IsNormal()
    {
        Assert.Equal(LoginState.Normal, LoginStatePolicy.Classify(
            hasRefreshToken: true, expiresAtMs: Now + OneHour, refreshTokenExpiresAtMs: Now + 30 * OneHour, nowMs: Now));
    }

    [Fact]
    public void HealthyToken_RefreshTokenExpiresAtAbsent_IsStillNormal()
    {
        // Older credential schemas may not carry refreshTokenExpiresAt at all (0/absent) -- must
        // not be misread as "already lapsed"; only expiresAtMs<=0 or a genuinely-past value counts.
        Assert.Equal(LoginState.Normal, LoginStatePolicy.Classify(
            hasRefreshToken: true, expiresAtMs: Now + OneHour, refreshTokenExpiresAtMs: 0, nowMs: Now));
    }

    [Fact]
    public void ExpiringSoonButStillLive_IsNormal_NotDead()
    {
        // A token that's about to expire (the ordinary proactive-refresh case) must stay Normal,
        // not be swept into Dead -- only expiresAt<=0 or a lapsed refresh token qualifies.
        Assert.Equal(LoginState.Normal, LoginStatePolicy.Classify(
            hasRefreshToken: true, expiresAtMs: Now + 1000, refreshTokenExpiresAtMs: Now + 30 * OneHour, nowMs: Now));
    }
}
