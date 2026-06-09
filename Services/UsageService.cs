using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

public sealed class UsageService
{
    private static readonly HttpClient _http = new();

    public async Task<UsageData> FetchAsync()
    {
        var token = await ReadTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Add("User-Agent", "claude-code/2.1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        return Parse(body);
    }

    // Generic, tolerant parse. We never deserialize into fixed records, so unknown
    // top-level fields (new models/windows) flow straight through. Any malformed or
    // unexpected shape is skipped rather than throwing -- the poller must never crash.
    internal static UsageData Parse(string body)
    {
        var windows = new Dictionary<string, UsageWindow?>(StringComparer.Ordinal);
        ExtraUsageData? extra = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    // extra_usage is the billing card -- special-cased, not a window.
                    if (prop.NameEquals("extra_usage"))
                    {
                        extra = MapExtra(prop.Value);
                        continue;
                    }

                    // Preserve null windows (e.g. seven_day_opus: null) keyed by field
                    // name so the frontend can decide whether to show a placeholder.
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        windows[prop.Name] = null;
                        continue;
                    }

                    // A value is a "usage window" iff it's an object with a numeric
                    // utilization. resets_at is optional. Anything else is ignored.
                    if (TryMapWindow(prop.Value, out var w))
                        windows[prop.Name] = w;
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable body -- return whatever we managed to collect (likely empty).
        }

        // Log raw extra_usage so the field semantics (units) can be verified.
        System.Diagnostics.Debug.WriteLine(
            $"[ClaudeUsage] windows=[{string.Join(", ", windows.Keys)}] " +
            $"extra_usage_enabled={extra is not null}");

        return new UsageData(windows, extra, DateTimeOffset.UtcNow);
    }

    private static bool TryMapWindow(JsonElement el, out UsageWindow? window)
    {
        window = null;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number)
            return false;

        DateTimeOffset? resets = null;
        if (el.TryGetProperty("resets_at", out var r)
            && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out var dto))
            resets = dto;

        window = new UsageWindow(u.GetDouble(), resets);
        return true;
    }

    private static ExtraUsageData? MapExtra(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!(el.TryGetProperty("is_enabled", out var ie) && ie.ValueKind == JsonValueKind.True))
            return null;

        var monthly = GetNum(el, "monthly_limit");
        var used    = GetNum(el, "used_credits");
        if (monthly is null || used is null) return null;

        var currency = el.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? ""
            : "";

        return new ExtraUsageData(
            MonthlyLimit:    monthly.Value / 100.0,
            UsedCredits:     used.Value    / 100.0,
            Utilization:     GetNum(el, "utilization") ?? 0,
            Currency:        currency,
            RawMonthlyLimit: monthly.Value,
            RawUsedCredits:  used.Value);
    }

    private static double? GetNum(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    private static async Task<string> ReadTokenAsync()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".credentials.json");

        var json = await File.ReadAllTextAsync(path);
        var creds = JsonSerializer.Deserialize<CredentialsFile>(json);
        return creds?.ClaudeAiOauth?.AccessToken
            ?? throw new InvalidOperationException("Access token not found in credentials file.");
    }

    // --- credentials models ---

    private record CredentialsFile(
        [property: JsonPropertyName("claudeAiOauth")] OAuthCreds? ClaudeAiOauth);

    private record OAuthCreds(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("expiresAt")] long ExpiresAt);
}
