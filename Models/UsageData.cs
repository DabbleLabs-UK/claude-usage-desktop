namespace ClaudeUsage.Models;

public record UsageWindow(double Utilization, DateTimeOffset ResetsAt);

public record ExtraUsageData(
    double MonthlyLimit,   // display value (raw / 100)
    double UsedCredits,    // display value (raw / 100)
    double Utilization,
    string Currency,
    double RawMonthlyLimit,  // raw API value -- inspect at /api/usage to confirm units
    double RawUsedCredits);  // raw API value -- inspect at /api/usage to confirm units

public record UsageData(
    UsageWindow? FiveHour,
    UsageWindow? SevenDay,
    UsageWindow? SevenDayOpus,
    UsageWindow? SevenDaySonnet,
    ExtraUsageData? ExtraUsage,
    DateTimeOffset FetchedAt,
    bool IsStale = false);
