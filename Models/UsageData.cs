namespace ClaudeUsage.Models;

// A usage window from /api/oauth/usage. ResetsAt is nullable: some windows
// report utilization without a resets_at, and the API may omit it entirely.
public record UsageWindow(double Utilization, DateTimeOffset? ResetsAt);

// Active backoff state pushed to the UI while polling is throttled.
// ErrorType: "rate_limit" | "auth" | "other" | "refresh_rate_limited"
//   "refresh_rate_limited" is a calm waiting state (not a fail-ramp): the token endpoint is
//   rate-limiting our own refresh, so the app is waiting to adopt a host `claude` disk refresh.
public record BackoffInfo(
    string ErrorType,
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
