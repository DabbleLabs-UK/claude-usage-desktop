using System.Text.Json;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Pure, tolerant parser for the Codex usage response
// (GET https://chatgpt.com/backend-api/wham/usage). Kept dependency-free and separate from
// CodexUsageService so it can be unit-tested without any network/credential machinery.
//
// Philosophy mirrors UsageService.Parse: NOTHING is hardcoded to a specific lane. We scan every
// sub-object of `rate_limit` and treat any object carrying a numeric `used_percent` AND
// `limit_window_seconds` as a window. Null / absent lanes (secondary_window on a Plus plan)
// contribute no window -- that IS the untracked-window filtering at the source: an unpopulated
// lane never becomes an empty placeholder card. Any malformed or unexpected shape yields an
// empty window list rather than throwing, so the poller can never crash on a bad body.
public static class CodexUsageParser
{
    public static CodexUsageData Parse(string body, DateTimeOffset now)
    {
        var windows = new List<CodexWindow>();
        double? creditsBalance = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("rate_limit", out var rateLimit)
                    && rateLimit.ValueKind == JsonValueKind.Object)
                {
                    foreach (var lane in rateLimit.EnumerateObject())
                    {
                        if (TryMapWindow(lane.Value, now, out var window))
                            windows.Add(window);
                    }
                }

                // credits.balance only when the plan actually has credits (has_credits == true).
                if (root.TryGetProperty("credits", out var credits)
                    && credits.ValueKind == JsonValueKind.Object)
                {
                    var hasCredits = credits.TryGetProperty("has_credits", out var hc)
                                     && hc.ValueKind == JsonValueKind.True;
                    if (hasCredits && TryGetNum(credits, "balance", out var balance))
                        creditsBalance = balance;
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable body -- return whatever we managed to collect (an empty snapshot).
        }

        // Shortest window first (a 5-hour session lane before the weekly one) so cards read
        // naturally top-to-bottom regardless of the order the API listed the lanes in.
        windows.Sort((a, b) => a.LimitWindowSeconds.CompareTo(b.LimitWindowSeconds));
        return new CodexUsageData(windows, creditsBalance, now);
    }

    // A lane is a "window" iff it is an object carrying a numeric used_percent AND
    // limit_window_seconds. reset_at (absolute) wins; reset_after_seconds is the relative
    // fallback. A null / non-object lane returns false and is filtered out entirely.
    private static bool TryMapWindow(JsonElement el, DateTimeOffset now, out CodexWindow window)
    {
        window = null!;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetNum(el, "used_percent", out var used)) return false;
        if (!TryGetNum(el, "limit_window_seconds", out var windowSeconds)) return false;

        DateTimeOffset? resetAt = null;
        if (TryGetNum(el, "reset_at", out var resetUnix) && resetUnix > 0)
        {
            try { resetAt = DateTimeOffset.FromUnixTimeSeconds((long)resetUnix); }
            catch (ArgumentOutOfRangeException) { resetAt = null; }
        }
        else if (TryGetNum(el, "reset_after_seconds", out var resetAfter) && resetAfter > 0)
        {
            resetAt = now.AddSeconds(resetAfter);
        }

        window = new CodexWindow(used, (long)windowSeconds, resetAt);
        return true;
    }

    private static bool TryGetNum(JsonElement el, string name, out double value)
    {
        value = 0;
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
        {
            value = v.GetDouble();
            return true;
        }
        return false;
    }
}
