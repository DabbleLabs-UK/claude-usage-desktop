using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

var credPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".claude", ".credentials.json");

if (!File.Exists(credPath))
{
    Console.Error.WriteLine($"Credentials file not found: {credPath}");
    return 1;
}

var credJson = await File.ReadAllTextAsync(credPath);
var creds = JsonSerializer.Deserialize<CredentialsFile>(credJson);
var token = creds?.ClaudeAiOauth?.AccessToken;

if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("Could not extract accessToken from credentials file.");
    return 1;
}

var expiresAt = creds?.ClaudeAiOauth?.ExpiresAt ?? 0;
if (expiresAt > 0)
{
    var expiry = DateTimeOffset.FromUnixTimeMilliseconds(expiresAt);
    if (expiry < DateTimeOffset.UtcNow)
        Console.Error.WriteLine($"Warning: token expired at {expiry:u} -- run a CC command to refresh, then re-run.");
}

using var http = new HttpClient();
var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
request.Headers.Add("User-Agent", "claude-code/2.1.0");
request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

var response = await http.SendAsync(request);
var body = await response.Content.ReadAsStringAsync();

if (!response.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    Console.Error.WriteLine(body);
    if ((int)response.StatusCode == 401)
        Console.Error.WriteLine("-> Token expired. Run any CC command to refresh, then re-run.");
    if ((int)response.StatusCode == 429)
        Console.Error.WriteLine("-> 429 received. Check User-Agent header value.");
    return 1;
}

var usage = JsonSerializer.Deserialize<UsageResponse>(body);
if (usage is null)
{
    Console.Error.WriteLine("Failed to parse usage response.");
    Console.Error.WriteLine(body);
    return 1;
}

static string FormatBucket(UsageBucket? b) =>
    b is null ? "(none)" : $"{b.Utilization:F1}%  (resets {b.ResetsAt})";

Console.WriteLine($"5-hour:  {FormatBucket(usage.FiveHour)}");
Console.WriteLine($"7-day:   {FormatBucket(usage.SevenDay)}");
Console.WriteLine($"Sonnet:  {FormatBucket(usage.SevenDaySonnet)}");
Console.WriteLine($"Opus:    {FormatBucket(usage.SevenDayOpus)}");

if (usage.ExtraUsage is { IsEnabled: true } eu)
{
    Console.WriteLine($"Billing: {eu.UsedCredits:F2} / {eu.MonthlyLimit} {eu.Currency}  ({eu.Utilization:F1}% of monthly limit)");
}

return 0;

// --- models ---

record CredentialsFile(
    [property: JsonPropertyName("claudeAiOauth")] OAuthCreds? ClaudeAiOauth);

record OAuthCreds(
    [property: JsonPropertyName("accessToken")] string? AccessToken,
    [property: JsonPropertyName("expiresAt")] long ExpiresAt);

record UsageResponse(
    [property: JsonPropertyName("five_hour")] UsageBucket? FiveHour,
    [property: JsonPropertyName("seven_day")] UsageBucket? SevenDay,
    [property: JsonPropertyName("seven_day_opus")] UsageBucket? SevenDayOpus,
    [property: JsonPropertyName("seven_day_sonnet")] UsageBucket? SevenDaySonnet,
    [property: JsonPropertyName("extra_usage")] ExtraUsage? ExtraUsage);

record UsageBucket(
    [property: JsonPropertyName("utilization")] double Utilization,
    [property: JsonPropertyName("resets_at")] string? ResetsAt);

record ExtraUsage(
    [property: JsonPropertyName("is_enabled")] bool IsEnabled,
    [property: JsonPropertyName("monthly_limit")] double MonthlyLimit,
    [property: JsonPropertyName("used_credits")] double UsedCredits,
    [property: JsonPropertyName("utilization")] double Utilization,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("disabled_reason")] string? DisabledReason);
