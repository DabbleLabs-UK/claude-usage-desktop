using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeUsage.Services;

public sealed record StatsWindow(string Range, DateTimeOffset From, DateTimeOffset To)
{
    public static bool TryResolve(string? range, string? end, DateTimeOffset now,
        out StatsWindow window)
    {
        window = null!;
        range ??= "1d";
        var days = range switch { "1d" => 1, "7d" => 7, "30d" => 30, _ => 0 };
        if (days == 0) return false;
        var to = now.ToUniversalTime();
        if (end is not null)
        {
            // Require an unambiguous ISO instant, including its UTC offset.
            if (end.Length > 40 || !Regex.IsMatch(end,
                    @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\z") ||
                !DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed)) return false;
            to = parsed.ToUniversalTime();
            if (to > now) to = now.ToUniversalTime();
        }
        if (to < DateTimeOffset.MinValue.AddDays(days)) return false;
        window = new(range, to.AddDays(-days), to);
        return true;
    }
}
