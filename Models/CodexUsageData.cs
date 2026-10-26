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
// can never degrade or stale the Claude lane (or vice versa).
public record CodexUsageData(
    IReadOnlyList<CodexWindow> Windows,
    double? CreditsBalance,
    DateTimeOffset FetchedAt,
    bool IsStale = false,
    bool CreditsUnlimited = false,
    CodexCreditsHistory? CreditsHistory = null);

public record CodexCreditPace(
    string Status,
    double? CreditsSpent,
    double? CreditsPerDay,
    double? EstimatedDaysLeft,
    double ObservedHours);

public record CodexCreditDay(string Date, double? CreditsSpent);

public record CodexCreditsHistory(
    CodexCreditPace Last24Hours,
    CodexCreditPace Last7Days,
    IReadOnlyList<CodexCreditDay> Daily);

// Independent connectivity/auth state for the Codex lane -- deliberately NOT the Claude
// ConnectivityState, so the two can't be confused and a Codex problem stays contained.
//   NoToken   -- no Codex login found on disk/keystore. Cached cards may remain stale.
//   Live      -- last poll succeeded; no banner.
//   SignedOut -- legacy combined state: expired cached token OR usage endpoint 401/403.
//                Reason distinguishes those observations without inferring subscription status.
//                Codex owns token refresh; we NEVER refresh or write credentials.
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

// Safe observations only, not diagnoses inferred from an HTTP rejection or response body.
public enum CodexFailureReason
{
    NoLocalCredentials,
    LocalTokenExpired,
    UsageUnauthorized,
    UsageForbidden,
}

// No credential source, account identity or upstream body is sent to the UI.
// The next attempt is approximate: the unchanged delay follows delivery/logging of this result.
public record CodexStatus(
    CodexState State,
    CodexFailureReason? Reason = null,
    int? HttpStatusCode = null,
    DateTimeOffset? CheckedAt = null,
    DateTimeOffset? NextAttemptAt = null);
