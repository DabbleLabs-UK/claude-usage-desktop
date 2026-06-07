namespace ClaudeUsage.Models;

public record UsageWindow(double Utilization, DateTimeOffset ResetsAt);

public record ExtraUsageData(
    double MonthlyLimit,
    double UsedCredits,
    double Utilization,
    string Currency);

public record UsageData(
    UsageWindow? FiveHour,
    UsageWindow? SevenDay,
    UsageWindow? SevenDayOpus,
    UsageWindow? SevenDaySonnet,
    ExtraUsageData? ExtraUsage,
    DateTimeOffset FetchedAt,
    bool IsStale = false);
