using System.Net;
using System.Net.Http.Headers;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Read-only Codex usage fetcher. An INDEPENDENT failure domain from Claude: it shares no state,
// no HttpClient error handling and no backoff with UsageService, so a Codex auth failure, network
// error or endpoint change can only ever produce a Codex state -- it can never stall, degrade or
// mark stale the Claude poll (and vice versa).
//
// SAFETY: this NEVER writes to auth.json and NEVER refreshes a token. Codex's own CLI owns that
// file and its rotation; racing it could corrupt the user's login. On any miss (missing token,
// expired token, or a failed call) we degrade gracefully to a typed CodexState.
public sealed class CodexUsageService
{
    private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";

    // Own HttpClient instance -- deliberately not shared with UsageService, to keep the lanes fully
    // separate (its timeouts / connection state can't influence Claude polling).
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public sealed record CodexResult(CodexState State, CodexUsageData? Data, string Source, string Detail);

    public async Task<CodexResult> FetchAsync()
    {
        var creds = CodexCredentialStore.Resolve(out var source);
        if (creds is null)
            return new CodexResult(CodexState.NoToken, null, "none", "no Codex login found");

        // Read-only expiry short-circuit: if the cached token is already expired we do NOT call the
        // endpoint (a guaranteed 401) and, above all, NEVER refresh -- Codex owns that. Surface an
        // honest signed-out so the user re-auths via the Codex CLI. Unknown expiry (opaque token,
        // exp == 0) falls through to the live call.
        if (creds.ExpiresAtMs > 0 && creds.ExpiresAtMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            return new CodexResult(CodexState.SignedOut, null, source, "cached Codex token expired");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(creds.AccountId))
                request.Headers.Add("ChatGPT-Account-Id", creds.AccountId);

            using var response = await _http.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new CodexResult(CodexState.SignedOut, null, source, $"HTTP {(int)response.StatusCode} from usage endpoint");
            if (!response.IsSuccessStatusCode)
                return new CodexResult(CodexState.Error, null, source, $"HTTP {(int)response.StatusCode} from usage endpoint");

            var body = await response.Content.ReadAsStringAsync();
            var data = CodexUsageParser.Parse(body, DateTimeOffset.UtcNow);
            return new CodexResult(CodexState.Live, data, source, $"{data.Windows.Count} window(s)");
        }
        catch (HttpRequestException ex)
        {
            // No HTTP response -- DNS/route/refused/TLS. Local connectivity loss, not an auth fault.
            return new CodexResult(CodexState.NoNetwork, null, source,
                $"request failed: {ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
        }
        catch (TaskCanceledException)
        {
            // HttpClient timeout (no cancellation token passed here) -- treat as offline.
            return new CodexResult(CodexState.NoNetwork, null, source, "request timed out");
        }
        catch (Exception ex)
        {
            return new CodexResult(CodexState.Error, null, source, $"unexpected {ex.GetType().Name}");
        }
    }
}
