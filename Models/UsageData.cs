namespace ClaudeUsage.Models;

// A usage window from /api/oauth/usage. ResetsAt is nullable: some windows
// report utilization without a resets_at, and the API may omit it entirely.
public record UsageWindow(double Utilization, DateTimeOffset? ResetsAt);

// The single, typed connectivity state surfaced to the UI. Replaces the old free-text errorType
// magic strings so server and client can't drift. Serialized as camelCase strings on the wire
// (see the JsonStringEnumConverter wiring in App.BuildWebApp): "live"/"noNetwork"/etc.
//
//   NoNetwork   -- local connectivity loss (DNS/route/refused/timeout, or no adapter up). Calm,
//                  no backoff ramp; recovers when the link returns (a NetworkChange event re-probes).
//   RateLimited -- the token/usage endpoint is rate-limiting; the app is waiting (no ramp for the
//                  refresh-cooldown case). Recovers when the host refreshes on disk.
//   AuthError   -- the saved Claude sign-in is invalid/expired (a 401 the refresh couldn't fix) or
//                  unreadable. Grace-then-prompt: first couple of misses read as "reconnecting",
//                  then "sign in again". Also the fallback for otherwise-unclassified failures.
//   Live        -- normal; in practice no BackoffInfo is sent (the banner is simply hidden).
public enum ConnectivityState
{
    Live,
    NoNetwork,
    RateLimited,
    AuthError,
}

// Active backoff/waiting state pushed to the UI while polling is not LIVE. State drives the calm
// banner; ConsecutiveFailures still distinguishes the auth grace window from the sign-in prompt.
public record BackoffInfo(
    ConnectivityState State,
    int ConsecutiveFailures,
    int IntervalSeconds,
    DateTimeOffset NextAttemptAt);

public record ExtraUsageData(
    double MonthlyLimit,   // display value (raw / 100)
    double UsedCredits,    // display value (raw / 100)
    double Utilization,
    string Currency,
    double RawMonthlyLimit,  // raw API value -- inspect at /api/usage to confirm units
    double RawUsedCredits);  // raw API value -- inspect at /api/usage to confirm units

// Generic pass-through of the usage response. Every top-level field that looks
// like a usage window (or is null) is carried in Windows keyed by its ORIGINAL
// API field name (e.g. "five_hour", "seven_day_opus"), so new windows Anthropic
// adds appear automatically without code changes. Null entries are preserved so
// the frontend can decide whether to show a "not tracked" placeholder. extra_usage
// stays special-cased as the billing card.
public record UsageData(
    IReadOnlyDictionary<string, UsageWindow?> Windows,
    ExtraUsageData? ExtraUsage,
    DateTimeOffset FetchedAt,
    bool IsStale = false);
