namespace ClaudeUsage.Models;

// A usage window from /api/oauth/usage. ResetsAt is nullable: some windows
// report utilization without a resets_at, and the API may omit it entirely.
public record UsageWindow(double Utilization, DateTimeOffset? ResetsAt);

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
