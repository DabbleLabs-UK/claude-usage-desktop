namespace ClaudeUsage.Models;

// One Codex rate-limit window, parsed from the rate_limit block of
// GET https://chatgpt.com/backend-api/wham/usage. UsedPercent mirrors used_percent;
// LimitWindowSeconds is the window length the CARD TITLE is derived from (we never assume
// which lane is "primary"/"secondary"); ResetAt is the absolute reset instant (from reset_at
// unix-seconds, or now + reset_after_seconds when only the relative value is present).
//
// Generic pass-through, mirroring the Claude window parser's philosophy: every populated
// sub-object of rate_limit becomes a window; a null lane (e.g. secondary_window on a Plus
// plan) simply contributes nothing -- it is filtered out at the source rather than carried as
// an empty placeholder.
public record CodexWindow(
    double UsedPercent,
    long LimitWindowSeconds,
    DateTimeOffset? ResetAt);

// A Codex usage snapshot. Fully INDEPENDENT of the Claude UsageData failure domain: it shares
// no state, no polling and no backoff with UsageService, so a Codex auth/network/endpoint fault
// can never degrade or stale the Claude lane (or vice versa). CreditsBalance is surfaced only
// when the response's credits.has_credits is true.
public record CodexUsageData(
    IReadOnlyList<CodexWindow> Windows,
    double? CreditsBalance,
    DateTimeOffset FetchedAt,
    bool IsStale = false);

// Independent connectivity/auth state for the Codex lane -- deliberately NOT the Claude
// ConnectivityState, so the two can't be confused and a Codex problem stays contained.
//   NoToken   -- no Codex login found on disk/keystore. The whole Codex section is hidden.
//   Live      -- last poll succeeded; no banner.
//   SignedOut -- the cached token is expired, or the endpoint returned 401/403. Codex owns
//                token refresh (its own CLI); we NEVER refresh -- the user re-auths via `codex`.
//   NoNetwork -- local connectivity loss, or the request failed to reach the endpoint / timed out.
//   Error     -- any other failure (5xx, malformed body, unexpected shape).
public enum CodexState
{
    NoToken,
    Live,
    SignedOut,
    NoNetwork,
    Error,
}

// Pushed to the UI so the Codex banner can react independently of the Claude banner.
public record CodexStatus(CodexState State);
