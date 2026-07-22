namespace ClaudeUsage.Services;

// Pure, dependency-free classification of the on-disk/keystore token state, used to decide
// whether FetchAsync should even attempt a refresh. Kept separate from UsageService so it can
// be unit-tested in isolation -- the same pattern as RefreshCooldownPolicy.
//
// THREE STATES:
//   Static -- no refresh token at all (env/settings override, or a `claude setup-token`).
//             Nothing to refresh; the existing no-op path. It genuinely has no expiry, so
//             expiresAt==0 here is meaningless, not a signal.
//   Dead   -- a refresh token IS present but there is nothing left to refresh FROM: either
//             expiresAt is 0/unset -- Claude Code's own signal that the LOGIN ITSELF has
//             lapsed (it writes literal 0 on a dead login, never as a live token's real
//             expiry) -- or the refresh token has its own expiry (refreshTokenExpiresAt) and
//             that has already passed. Neither the CLI (`claude -p`) nor our own HTTP refresh
//             can fix this; only an interactive `/login` can. Before this policy existed,
//             expiresAt==0 with a refresh token present fell through IsExpiringSoon's "unknown
//             expiry, don't guess" guard (meant for the Static case) and was silently never
//             refreshed -- the app polled a dead token forever, eventually looping into the
//             sticky-cooldown "waiting on Claude Code to refresh" banner with nothing ever
//             actually coming.
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
        if (refreshTokenExpiresAtMs > 0 && refreshTokenExpiresAtMs <= nowMs) return LoginState.Dead;
        return LoginState.Normal;
    }
}
