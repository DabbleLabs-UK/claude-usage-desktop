using System.Net.Http;
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
        var raw = JsonSerializer.Deserialize<RawUsageResponse>(body)
            ?? throw new InvalidOperationException("Empty usage response.");

        return Map(raw);
    }

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

    private static UsageData Map(RawUsageResponse raw) => new(
        FiveHour: Map(raw.FiveHour),
        SevenDay: Map(raw.SevenDay),
        SevenDayOpus: Map(raw.SevenDayOpus),
        SevenDaySonnet: Map(raw.SevenDaySonnet),
        ExtraUsage: raw.ExtraUsage is { IsEnabled: true } eu
            ? new ExtraUsageData(eu.MonthlyLimit, eu.UsedCredits, eu.Utilization, eu.Currency ?? "")
            : null,
        FetchedAt: DateTimeOffset.UtcNow);

    private static UsageWindow? Map(RawWindow? w) =>
        w is null ? null : new(w.Utilization, DateTimeOffset.Parse(w.ResetsAt ?? "1970-01-01T00:00:00Z"));

    // --- raw API models ---

    private record CredentialsFile(
        [property: JsonPropertyName("claudeAiOauth")] OAuthCreds? ClaudeAiOauth);

    private record OAuthCreds(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("expiresAt")] long ExpiresAt);

    private record RawUsageResponse(
        [property: JsonPropertyName("five_hour")] RawWindow? FiveHour,
        [property: JsonPropertyName("seven_day")] RawWindow? SevenDay,
        [property: JsonPropertyName("seven_day_opus")] RawWindow? SevenDayOpus,
        [property: JsonPropertyName("seven_day_sonnet")] RawWindow? SevenDaySonnet,
        [property: JsonPropertyName("extra_usage")] RawExtraUsage? ExtraUsage);

    private record RawWindow(
        [property: JsonPropertyName("utilization")] double Utilization,
        [property: JsonPropertyName("resets_at")] string? ResetsAt);

    private record RawExtraUsage(
        [property: JsonPropertyName("is_enabled")] bool IsEnabled,
        [property: JsonPropertyName("monthly_limit")] double MonthlyLimit,
        [property: JsonPropertyName("used_credits")] double UsedCredits,
        [property: JsonPropertyName("utilization")] double Utilization,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("disabled_reason")] string? DisabledReason);
}
