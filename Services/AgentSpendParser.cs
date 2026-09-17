using System.Globalization;
using System.Text.Json;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Pure, tolerant parser for the Agents & Inference spend-summary contract file
// (%LOCALAPPDATA%\DabbleLabs\AgentsInference\spend-summary.v1.json). Mirrors CodexUsageParser's
// philosophy: NEVER throws out of Parse(), degrades to null on anything unparseable or missing a
// required top-level field, so a bad/partial file can never crash the poller or disturb the
// Claude/Codex lanes. This does NOT reimplement any rolling-window, pricing, run-rate or
// model/provider aggregation -- every number is read straight off the file, not derived (the one
// exception being simple display arithmetic done in the frontend, e.g. an average-cost-per-run
// division -- never here).
public static class AgentSpendParser
{
    public static AgentSpendData? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!TryGetStr(root, "schema_version", out var schemaVersion)) return null;
            if (!TryGetDate(root, "generated_at", out var generatedAt)) return null;

            var currency = TryGetStr(root, "currency", out var cur) ? cur! : "USD";
            var dataThrough = TryGetStr(root, "data_through", out var dt) ? dt : null;
            var timezone = TryGetStr(root, "timezone", out var tz) ? tz : null;

            var source = ParseSource(root);
            if (source is null) return null;

            var rolling24h = ParseRolling24h(root);
            var rolling7d = ParseRolling7d(root);
            var mtd = ParseMonthToDate(root);
            var runRates = ParseRunRates(root);
            if (rolling24h is null || rolling7d is null || mtd is null || runRates is null) return null;

            return new AgentSpendData(
                schemaVersion!,
                generatedAt,
                dataThrough,
                currency,
                timezone,
                source,
                rolling24h,
                rolling7d,
                mtd,
                runRates,
                ParseModelBreakdown(root),
                ParseProviderBreakdown(root),
                ParseDailyHistory(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AgentSpendSource? ParseSource(JsonElement root)
    {
        if (!root.TryGetProperty("source", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        if (!TryGetStr(s, "status", out var status)) return null;
        var lastSuccess = TryGetDate(s, "last_success_at", out var ls) ? ls : (DateTimeOffset?)null;
        return new AgentSpendSource(status!, lastSuccess);
    }

    private static AgentSpendRolling24h? ParseRolling24h(JsonElement root)
    {
        if (!root.TryGetProperty("rolling_24h", out var w) || w.ValueKind != JsonValueKind.Object) return null;
        return new AgentSpendRolling24h(GetNum(w, "total_cost"), GetInt(w, "priced_runs"), GetInt(w, "unpriced_runs"));
    }

    private static AgentSpendRolling7d? ParseRolling7d(JsonElement root)
    {
        if (!root.TryGetProperty("rolling_7d", out var w) || w.ValueKind != JsonValueKind.Object) return null;
        return new AgentSpendRolling7d(
            GetNum(w, "total_cost"), GetNum(w, "daily_average"), GetInt(w, "priced_runs"), GetInt(w, "unpriced_runs"));
    }

    private static AgentSpendMonthToDate? ParseMonthToDate(JsonElement root)
    {
        if (!root.TryGetProperty("month_to_date", out var w) || w.ValueKind != JsonValueKind.Object) return null;
        return new AgentSpendMonthToDate(GetNum(w, "total_cost"), GetInt(w, "priced_runs"), GetInt(w, "unpriced_runs"));
    }

    private static AgentSpendRunRates? ParseRunRates(JsonElement root)
    {
        if (!root.TryGetProperty("run_rates", out var w) || w.ValueKind != JsonValueKind.Object) return null;
        return new AgentSpendRunRates(GetNum(w, "monthly_at_24h_pace"), GetNum(w, "monthly_at_7d_pace"));
    }

    private static IReadOnlyList<AgentSpendModelBreakdown> ParseModelBreakdown(JsonElement root)
    {
        var list = new List<AgentSpendModelBreakdown>();
        if (root.TryGetProperty("breakdown_by_model", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetStr(item, "model", out var model)) continue;
                var provider = TryGetStr(item, "provider", out var p) ? p : null;
                list.Add(new AgentSpendModelBreakdown(
                    model!,
                    provider,
                    GetNum(item, "cost"),
                    GetInt(item, "priced_runs"),
                    GetInt(item, "unpriced_runs"),
                    GetLong(item, "input_tokens"),
                    GetLong(item, "output_tokens")));
            }
        }
        return list;
    }

    private static IReadOnlyList<AgentSpendProviderBreakdown> ParseProviderBreakdown(JsonElement root)
    {
        var list = new List<AgentSpendProviderBreakdown>();
        if (root.TryGetProperty("breakdown_by_provider", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetStr(item, "provider", out var provider)) continue;
                list.Add(new AgentSpendProviderBreakdown(
                    provider!, GetNum(item, "cost"), GetInt(item, "priced_runs"), GetInt(item, "unpriced_runs")));
            }
        }
        return list;
    }

    private static IReadOnlyList<AgentSpendDailyPoint> ParseDailyHistory(JsonElement root)
    {
        var list = new List<AgentSpendDailyPoint>();
        if (root.TryGetProperty("daily_history", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetStr(item, "date", out var date)) continue;
                list.Add(new AgentSpendDailyPoint(
                    date!, GetNum(item, "cost"), GetInt(item, "priced_runs"), GetInt(item, "unpriced_runs")));
            }
        }
        return list;
    }

    private static bool TryGetStr(JsonElement el, string name, out string? value)
    {
        value = null;
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString();
            return value is not null;
        }
        return false;
    }

    private static bool TryGetDate(JsonElement el, string name, out DateTimeOffset value)
    {
        value = default;
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private static double GetNum(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static int GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
