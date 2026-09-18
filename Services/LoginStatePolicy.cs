namespace ClaudeUsage.Services;

// Pure, dependency-free classification of the on-disk/keystore token state, used to decide
// whether FetchAsync should even attempt a refresh. Kept separate from UsageService so it can
// be unit-tested in isolation -- the same pattern as RefreshCooldownPolicy.
//
// THREE STATES:
//   Static -- no refresh token at all (env/settings override, or a `claude setup-token`).
//             Nothing to refresh; the existing no-op path. It genuinely has no expiry, so
//             expiresAt==0 here is meaningless, not a signal.
//   Dead   -- the access token is already unusable AND there is no usable refresh token: either
//             expiresAt is 0/unset -- Claude Code's own signal that the LOGIN ITSELF has lapsed
//             -- or both positive expiries are past. A lapsed refresh token must NOT make a still
//             live access token look signed out: polling can continue until that access token
//             expires, it just cannot be renewed automatically.
//   Normal -- a live token, possibly stale/expiring, handled by the existing refresh machinery
//             (proactive skew refresh, reactive 401 refresh, cooldown, etc).
internal enum LoginState { Static, Dead, Normal }

internal static class LoginStatePolicy
{
    internal static LoginState Classify(
        bool hasRefreshToken, long expiresAtMs, long refreshTokenExpiresAtMs, long nowMs)
    {
        if (!hasRefreshToken) return LoginState.Static;
        if (expiresAtMs <= 0) return LoginState.Dead;
        if (expiresAtMs <= nowMs && !CanRefresh(hasRefreshToken, refreshTokenExpiresAtMs, nowMs))
            return LoginState.Dead;
        return LoginState.Normal;
    }

    internal static bool CanRefresh(bool hasRefreshToken, long refreshTokenExpiresAtMs, long nowMs) =>
        hasRefreshToken && (refreshTokenExpiresAtMs <= 0 || refreshTokenExpiresAtMs > nowMs);
}
