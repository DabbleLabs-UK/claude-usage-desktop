using System.Globalization;
using System.Text.Json;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Reads only the account-wide export. The old launcher/key-scoped file is never a fallback.
// Validation precedes the tolerant shared parser so absent amounts cannot silently become zero.
public static class OpenRouterSpendReader
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeUsage", "openrouter-spend.v1.json");

    public enum ReadStatus { Ok, Missing, Unavailable, Malformed }

    public sealed record ReadResult(
        ReadStatus Status,
        AgentSpendData? Data = null,
        string? AccountScopeId = null,
        string? Detail = null,
        bool ClearCachedData = false);

    // The delegate permits file-failure tests without reading real files or credentials.
    public static ReadResult Read(string? path = null, Func<string, string>? readAllText = null)
    {
        try
        {
            return Parse((readAllText ?? File.ReadAllText)(path ?? FilePath));
        }
        catch (FileNotFoundException) { return new(ReadStatus.Missing); }
        catch (DirectoryNotFoundException) { return new(ReadStatus.Missing); }
        catch { return new(ReadStatus.Unavailable, Detail: "Account spend export could not be read."); }
    }

    public static ReadResult Parse(string json)
    {
        string? accountScopeId = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!IsObject(root) || !Integer(root, "schema_version", out var version) || version != 1
                || !Label(root, "scope", out var scope) || scope != "openrouter_account")
                return Invalid();

            if (Label(root, "account_scope_id", out var identity)
                && identity.Length == 64 && identity.All(Uri.IsHexDigit))
                accountScopeId = identity;

            if (!Object(root, "source", out var source) || !Label(source, "status", out var status)
                || (status != "ok" && status != "error"))
                return Invalid(accountScopeId);

            // A failed first sync can publish a minimal marker, without fabricated zero totals.
            // If its account is unknown (for example after reconfiguration), never retain figures
            // belonging to a previously configured account.
            if (status == "error" && !root.TryGetProperty("generated_at", out _))
                return new(ReadStatus.Unavailable, AccountScopeId: accountScopeId,
                    Detail: "Account spend source is unavailable.", ClearCachedData: accountScopeId is null);

            if (accountScopeId is null || !source.TryGetProperty("last_success_at", out var lastSuccess)
                || (lastSuccess.ValueKind != JsonValueKind.Null && !Date(source, "last_success_at", out _)))
                return Invalid(accountScopeId);

            if (!Date(root, "generated_at", out _) || !Label(root, "currency", out var currency)
                || currency != "USD" || !Label(root, "timezone", out _)
                || !Totals(root, "rolling_24h", out _)
                || !Totals(root, "rolling_7d", out var week) || !Number(week, "daily_average")
                || !Totals(root, "month_to_date", out _)
                || !Object(root, "run_rates", out var rates)
                || !Number(rates, "monthly_at_24h_pace") || !Number(rates, "monthly_at_7d_pace")
                || !Totals(root, "calendar_week", out var calendarWeek)
                || !Date(calendarWeek, "starts_at", out var startsAt)
                || !Rows(root, "breakdown_by_model", ModelRow)
                || !Rows(root, "breakdown_by_provider", ProviderRow)
                || !Rows(root, "daily_history", DailyRow))
                return Invalid(accountScopeId);

            if (root.TryGetProperty("data_through", out var through)
                && through.ValueKind != JsonValueKind.Null && !Date(root, "data_through", out _))
                return Invalid(accountScopeId);

            var data = AgentSpendParser.Parse(json);
            if (data is null) return Invalid(accountScopeId);
            data = data with
            {
                CalendarWeek = new AgentSpendCalendarWeek(
                    calendarWeek.GetProperty("total_cost").GetDouble(),
                    calendarWeek.GetProperty("priced_runs").GetInt32(),
                    calendarWeek.GetProperty("unpriced_runs").GetInt32(), startsAt)
            };
            return new(status == "ok" ? ReadStatus.Ok : ReadStatus.Unavailable, data, accountScopeId,
                status == "ok" ? null : "Account spend source is unavailable.");
        }
        catch { return Invalid(accountScopeId); }
    }

    private static ReadResult Invalid(string? accountScopeId = null) =>
        new(ReadStatus.Malformed, AccountScopeId: accountScopeId, Detail: "Account spend export is invalid.");

    private static bool IsObject(JsonElement value) => value.ValueKind == JsonValueKind.Object;

    private static bool Object(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value) && IsObject(value);

    private static bool Label(JsonElement parent, string name, out string value)
    {
        value = "";
        if (!parent.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString()!;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    }

    private static bool Date(JsonElement parent, string name, out DateTimeOffset value)
    {
        value = default;
        return Label(parent, name, out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    private static bool Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number
        && item.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0;

    private static bool Integer(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number
            && item.TryGetInt32(out value) && value >= 0;
    }

    private static bool Tokens(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number
        && item.TryGetInt64(out var value) && value >= 0;

    private static bool Counts(JsonElement row) =>
        Integer(row, "priced_runs", out _) && Integer(row, "unpriced_runs", out _);

    private static bool Totals(JsonElement root, string name, out JsonElement value) =>
        Object(root, name, out value) && Number(value, "total_cost") && Counts(value);

    private static bool Rows(JsonElement root, string name, Func<JsonElement, bool> validate) =>
        root.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array
        && items.EnumerateArray().All(item => IsObject(item) && validate(item));

    private static bool ModelRow(JsonElement row) =>
        Label(row, "model", out _) && Label(row, "provider", out _) && Number(row, "cost")
        && Counts(row) && Tokens(row, "input_tokens") && Tokens(row, "output_tokens");

    private static bool ProviderRow(JsonElement row) =>
        Label(row, "provider", out _) && Number(row, "cost") && Counts(row);

    private static bool DailyRow(JsonElement row) =>
        Label(row, "date", out var date)
        && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
        && Number(row, "cost") && Counts(row);
}
