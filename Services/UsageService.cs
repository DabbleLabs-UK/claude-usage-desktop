using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeUsage.Models;
using Microsoft.Extensions.Logging;

namespace ClaudeUsage.Services;

public sealed class UsageService
{
    // Official Claude Code CLI OAuth client id + the documented refresh endpoint.
    private const string OAuthClientId   = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string RefreshEndpoint = "https://platform.claude.com/v1/oauth/token";

    // Proactively refresh when the access token is expired or within this window of expiry.
    private const long RefreshSkewMs = 5 * 60 * 1000; // 5 minutes

    // Refresh-specific cooldown after a 429 from the TOKEN endpoint. Without this the poller
    // re-fires a proactive refresh every cycle once the token is expired; each attempt resets
    // the endpoint's STICKY rate-limit clock, so the app locks ITSELF out indefinitely. The
    // duration policy (Retry-After, else a multi-hour sticky ramp) lives in RefreshCooldownPolicy.
    // During cooldown NO own network refresh (proactive or reactive) may fire -- but DISK-ADOPTION
    // (TryAdoptFresherDiskTokenAsync, a local file read) is NEVER gated by the cooldown, so a host
    // `claude` refresh still recovers the app promptly without any token-endpoint call. Disk
    // adoption is the PRIMARY recovery path; our own network refresh is a rare last resort.

    private static readonly HttpClient _http = new();
    private readonly ILogger<UsageService> _logger;
    private readonly AuthRefreshLog _authLog;

    // Cooldown state. Mutated only from FetchAsync/TryRefreshAsync, which the single
    // background poller calls strictly sequentially (never concurrently), so plain fields
    // are sufficient -- no locking required.
    private long _refreshCooldownUntilMs;   // unix-ms; refresh suppressed until this instant
    private int  _consecutive429;           // consecutive token-endpoint 429s; drives the ramp

    public UsageService(ILogger<UsageService> logger, AuthRefreshLog authLog)
    {
        _logger  = logger;
        _authLog = authLog;
    }

    private static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    public async Task<UsageData> FetchAsync()
    {
        var creds = await ReadCredentialsAsync();

        // At most ONE refresh attempt per poll cycle (same burst-protection discipline as
        // the poller's backoff). Proactive consumes the attempt; if it ran, reactive won't.
        bool refreshAttempted = false;

        // --- PROACTIVE refresh -------------------------------------------------------------
        // If the token is already expired or about to expire, refresh BEFORE polling so the
        // poll uses a fresh token. A failed refresh here is swallowed (returns null) and we
        // fall through to poll with the existing token -- it may still work, or yield a 401
        // that the poller turns into the backoff/auth card.
        var nowMs        = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var remainingMs  = creds.ExpiresAt - nowMs;
        var hasRefresh   = creds.RefreshToken is not null;
        var expiringSoon = IsExpiringSoon(creds.ExpiresAt);
        _authLog.Log(
            $"POLL proactive-check: expiresAt={creds.ExpiresAt} ({FormatMs(creds.ExpiresAt)}) " +
            $"remaining={remainingMs}ms (~{remainingMs / 1000}s) skew={RefreshSkewMs}ms " +
            $"refreshTokenPresent={hasRefresh} expiringSoon={expiringSoon} " +
            $"=> proactiveTrigger={hasRefresh && expiringSoon}");

        if (creds.RefreshToken is not null && IsExpiringSoon(creds.ExpiresAt))
        {
            // FIX 1: prefer a token the host `claude` CLI already refreshed on disk. Re-read the
            // credentials file RIGHT NOW and, if disk carries a fresher token with real runway,
            // adopt it and SKIP the network refresh entirely -- this is what stops us competing
            // with claude for the same rate-limited token endpoint. Adoption is a local file read,
            // never a POST, so it is allowed even while a 429 cooldown suppresses network refresh.
            var adopted = await TryAdoptFresherDiskTokenAsync(creds);
            if (adopted is not null)
            {
                creds = adopted;   // poll below uses the disk token; no refresh fired
            }
            else if (IsInRefreshCooldown(out var remaining))
            {
                // Disk is ALSO stale and we're suppressed by an earlier 429. Do NOT spend the
                // attempt; let the poll fail to the poller's own backoff. This is the whole fix
                // for the self-inflicted 429.
                _authLog.Log($"PROACTIVE refresh SKIPPED: disk token also stale, in cooldown, {remaining / 1000}s remaining.");
            }
            else
            {
                // Genuine claude-idle case: BOTH in-memory and freshly-read disk tokens are within
                // the skew window. Now -- and only now -- run our own network refresh.
                refreshAttempted = true;
                _authLog.Log("PROACTIVE NETWORK-REFRESH firing (in-memory AND disk tokens both expired/within skew).");
                var refreshed = await TryRefreshAsync(creds.RefreshToken);
                if (refreshed is not null)
                {
                    creds = refreshed;
                    _authLog.Log("PROACTIVE refresh succeeded; polling with fresh token.");
                }
                else
                {
                    _authLog.Log("PROACTIVE refresh failed; polling with existing (likely expired) token.");
                }
            }
        }

        try
        {
            var data = await PollUsageAsync(creds.AccessToken);
            if (refreshAttempted)
                _authLog.Log("POLL ok after refresh this cycle -- recovery confirmed.");
            return data;
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == HttpStatusCode.Unauthorized
                  && !refreshAttempted
                  && creds.RefreshToken is not null)
        {
            // --- REACTIVE refresh ----------------------------------------------------------
            // The poll came back 401 and we have NOT already spent our refresh this cycle.
            // Try exactly one refresh, then retry the poll once. If the refresh fails, or the
            // retried poll still 401s, the exception propagates to the poller's backoff path.
            // FIX 1: before POSTing a refresh in response to the 401, re-read disk -- claude may
            // have refreshed the token since this poll started. If so, adopt it and retry the
            // poll with no network refresh (and ignoring any cooldown, since adoption is local).
            var adopted = await TryAdoptFresherDiskTokenAsync(creds);
            if (adopted is not null)
            {
                _authLog.Log("REACTIVE adopted fresher disk token; retrying poll WITHOUT network refresh.");
                return await PollUsageAsync(adopted.AccessToken);
            }
            if (IsInRefreshCooldown(out var remaining))
            {
                // Own-refresh is suppressed by the sticky 429 cooldown and disk has nothing
                // fresher. Surface this as a DISTINCT signal (not a bare 401) so the poller shows
                // "token endpoint rate-limited -- waiting on host refresh" and keeps polling at the
                // base cadence to re-check disk-adoption, rather than ramping an auth backoff.
                _authLog.Log($"REACTIVE refresh SUPPRESSED: disk token not fresher, in sticky cooldown, {remaining / 1000}s (~{remaining / 60000}min) remaining; surfacing refresh-rate-limited, NOT poking token endpoint.");
                throw new RefreshRateLimitedException(_refreshCooldownUntilMs);
            }
            _authLog.Log("REACTIVE NETWORK-REFRESH: poll 401, disk token not fresher, gate open, refresh token present => one reactive refresh.");
            _logger.LogWarning("Usage poll returned 401; attempting one token refresh.");
            var refreshed = await TryRefreshAsync(creds.RefreshToken);
            if (refreshed is null)
            {
                _authLog.Log("REACTIVE refresh failed; propagating 401 to poller backoff.");
                throw;                              // fall through to existing backoff/error card
            }
            _authLog.Log("REACTIVE refresh succeeded; retrying poll once with fresh token.");
            return await PollUsageAsync(refreshed.AccessToken); // a second 401 → backoff
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // 401 that the reactive path above did NOT handle: either the refresh gate was
            // already spent this cycle (proactive ran) or no refresh token exists. This is
            // the exact state that leaves the auth-error card stuck -- record why no retry.
            _authLog.Log(
                $"POLL 401 NOT retried: refreshAttempted={refreshAttempted} " +
                $"refreshTokenPresent={creds.RefreshToken is not null}; propagating to poller backoff.");
            throw;
        }
    }

    // FIX 1 helper -- "adopt-disk-token vs network-refresh" decision.
    //
    // Re-reads .credentials.json from disk and returns the disk credential set IFF it is a
    // genuinely fresher token than the one we are holding: a strictly later expiresAt AND
    // still outside the refresh-skew window (real runway, not itself about-to-expire). That
    // is exactly the "claude already refreshed it for us" case -- adopt it and skip our own
    // network refresh. Returns null when disk is no better (both stale -> caller falls through
    // to the real network-refresh path) or when the re-read fails (never throws).
    //
    // Freshness is detected purely from expiresAt: each writer stamps expiresAt = now + expires_in
    // on refresh, so a later expiresAt unambiguously means a newer token set.
    private async Task<Credentials?> TryAdoptFresherDiskTokenAsync(Credentials inMemory)
    {
        Credentials disk;
        try
        {
            disk = await ReadCredentialsAsync();
        }
        catch (Exception ex)
        {
            _authLog.LogException("DISK re-read for adoption FAILED (falling through to refresh path)", ex);
            return null;
        }

        var fresher   = disk.ExpiresAt > inMemory.ExpiresAt;
        var hasRunway = !IsExpiringSoon(disk.ExpiresAt);
        if (fresher && hasRunway)
        {
            _authLog.Log(
                $"ADOPT-DISK-TOKEN: disk expiresAt={disk.ExpiresAt} ({FormatMs(disk.ExpiresAt)}) > " +
                $"memory={inMemory.ExpiresAt} ({FormatMs(inMemory.ExpiresAt)}) and outside skew; " +
                $"adopting disk token, SKIPPING network refresh. access({Preview(disk.AccessToken)})");
            return disk;
        }

        _authLog.Log(
            $"DISK re-read: expiresAt={disk.ExpiresAt} ({FormatMs(disk.ExpiresAt)}) fresher={fresher} " +
            $"hasRunway={hasRunway}; NOT adopting -> network-refresh path.");
        return null;
    }

    private static async Task<UsageData> PollUsageAsync(string token)
    {
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

    // True iff the token is expired or within RefreshSkewMs of expiry. An unknown/zero
    // expiry returns false -- we don't proactively refresh on a guess; the reactive 401
    // path covers that case (and avoids hammering the refresh endpoint every cycle).
    private static bool IsExpiringSoon(long expiresAtMs)
    {
        if (expiresAtMs <= 0) return false;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return expiresAtMs - nowMs <= RefreshSkewMs;
    }

    // True while refresh is suppressed by a prior 429; remainingMs is the time left (>0).
    private bool IsInRefreshCooldown(out long remainingMs)
    {
        remainingMs = _refreshCooldownUntilMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return remainingMs > 0;
    }

    // Records a token-endpoint 429: bumps the consecutive counter, computes the cooldown
    // duration (Retry-After wins; otherwise the exponential ramp), and arms the suppression.
    private void EnterRefreshCooldown(HttpResponseMessage response)
    {
        _consecutive429++;
        var retryAfterMs = RetryAfterMs(response);
        var cooldownMs   = RefreshCooldownPolicy.ComputeMs(_consecutive429, retryAfterMs);
        _refreshCooldownUntilMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + cooldownMs;

        var basis = retryAfterMs is { } ra ? $"Retry-After={ra}ms" : $"sticky ramp (consecutive429={_consecutive429})";
        _authLog.Log(
            $"REFRESH 429 -> cooldown ENTERED: {cooldownMs}ms (~{cooldownMs / 60000}min) via {basis}; " +
            $"no OWN refresh until {FormatMs(_refreshCooldownUntilMs)} (disk-adoption still active).");
    }

    // Clears the cooldown and zeroes the consecutive-429 ramp. Called ONLY from a successful
    // own network refresh (HTTP 200) -- deliberately NOT from disk-adoption recovery. A host
    // `claude` refresh that we merely adopt does NOT prove our own refresh would now succeed
    // (the token endpoint may still be rate-limiting our IP), so the network-refresh backoff
    // must keep escalating across the whole event until WE get a real 200.
    private void ClearRefreshCooldown(string reason)
    {
        if (_refreshCooldownUntilMs != 0 || _consecutive429 != 0)
            _authLog.Log($"REFRESH cooldown CLEARED ({reason}).");
        _refreshCooldownUntilMs = 0;
        _consecutive429 = 0;
    }

    // Reads Retry-After as milliseconds: delta-seconds form or HTTP-date form. Null if absent;
    // a past date clamps to 0 (caller treats that as "no server-imposed wait").
    private static long? RetryAfterMs(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } d) return (long)d.TotalMilliseconds;
        if (ra.Date is { } when)
        {
            var ms = (when - DateTimeOffset.UtcNow).TotalMilliseconds;
            return ms > 0 ? (long)ms : 0;
        }
        return null;
    }

    // Performs the documented Claude Code refresh_token flow. Returns the new credential set
    // on success (already written back to disk), or null on ANY failure -- non-200 (incl. a
    // Cloudflare 403 / 429), malformed body, or thrown exception. Never throws to the caller,
    // never loops, and never logs token material.
    private async Task<Credentials?> TryRefreshAsync(string refreshToken)
    {
        try
        {
            _authLog.Log(
                $"REFRESH POST {RefreshEndpoint} content-type=application/json " +
                $"grant_type=refresh_token client_id={OAuthClientId} " +
                $"refresh_token({Preview(refreshToken)}).");

            var payload = new JsonObject
            {
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"]     = OAuthClientId,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, RefreshEndpoint)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("User-Agent", "claude-code/2.1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request);
            _authLog.Log($"REFRESH response: HTTP {(int)response.StatusCode} {response.StatusCode}.");
            if (!response.IsSuccessStatusCode)
            {
                // Body ON FAILURE ONLY -- token-endpoint errors are {"error":"invalid_grant"}
                // style payloads with no secret material; safe (and essential) to capture.
                var errBody = await SafeReadBodyAsync(response);
                _authLog.Log($"REFRESH FAILED -- response body: {errBody}");

                // A 429 from the token endpoint is the self-lockout trap: enter a refresh
                // cooldown so we stop re-arming the rate limit every poll. Other 4xx/5xx keep
                // the existing one-attempt-per-cycle behavior (the poll-interval is the floor).
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    EnterRefreshCooldown(response);

                // Log only the status code (e.g. 403 WAF, 429) -- never the body/tokens.
                _logger.LogWarning(
                    "Token refresh failed: HTTP {Status}. Falling back to backoff; a manual " +
                    "re-auth (run Claude Code) may be needed.", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            var (access, refresh, expiresIn) = ParseRefreshResponse(body);
            if (access is null)
            {
                _authLog.Log("REFRESH parse: 200 OK but access_token MISSING/blank in body; treating as failure.");
                _logger.LogWarning("Token refresh response missing access_token; ignoring.");
                return null;
            }

            // The refresh HTTP call succeeded -- whatever rate-limit state we were in is over.
            ClearRefreshCooldown("refresh succeeded");

            // Refresh tokens may ROTATE -- store whichever one comes back; keep the old one
            // only if the response omitted it entirely.
            var rotated      = refresh is not null && refresh != refreshToken;
            var newRefresh   = refresh ?? refreshToken;
            var newExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000;
            _authLog.Log(
                $"REFRESH parse ok: access_token({Preview(access)}) expires_in={expiresIn}s " +
                $"newExpiresAt={newExpiresAt} ({FormatMs(newExpiresAt)}) " +
                $"refreshTokenRotated={rotated} newRefresh({Preview(newRefresh)}).");

            try
            {
                await WriteCredentialsAsync(access, newRefresh, newExpiresAt);
                _authLog.Log("WRITE-BACK ok: .credentials.json atomically updated with rotated token set.");
            }
            catch (Exception wex)
            {
                // The fresh access_token is VALID in memory -- a persistence failure must NOT
                // throw it away. Use it for this poll anyway; it just won't survive a restart
                // (and CC may briefly disagree on the rotated token until the next good write).
                _authLog.LogException("WRITE-BACK FAILED (using in-memory fresh token for polling anyway; not persisted)", wex);
            }

            _logger.LogInformation("Token refreshed; credentials updated and CC kept in sync.");
            return new Credentials(access, newRefresh, newExpiresAt);
        }
        catch (Exception ex)
        {
            // A refresh failure must NEVER crash the poller. Log the exception type only.
            _authLog.LogException("REFRESH threw (falling back to backoff)", ex);
            _logger.LogWarning("Token refresh threw {Type}; falling back to backoff.", ex.GetType().Name);
            return null;
        }
    }

    // Reduces a secret to a non-reversible preview for logging -- NEVER the token itself.
    // "first 8 chars at most" per the instrumentation spec.
    private static string Preview(string? secret) =>
        secret is null ? "<null>"
        : $"len={secret.Length} first8={secret[..Math.Min(8, secret.Length)]}";

    // Renders a unix-ms instant as a readable UTC stamp; "unset" for a zero/absent expiry.
    private static string FormatMs(long unixMs) =>
        unixMs <= 0 ? "unset"
        : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + "Z";

    // Reads a (failure) response body defensively for logging: bounded length, never throws.
    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp)
    {
        try
        {
            var s = await resp.Content.ReadAsStringAsync();
            return s.Length > 2000 ? s[..2000] + "...(truncated)" : s;
        }
        catch (Exception ex)
        {
            return $"<body read failed: {ex.GetType().Name}>";
        }
    }

    private static (string? Access, string? Refresh, long ExpiresIn) ParseRefreshResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, 3600);

            string? access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() : null;
            string? refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;
            // Default to 1h if the field is absent -- avoids a 0 expiry that would force a
            // refresh on every subsequent cycle (i.e. hammering the endpoint).
            long expiresIn = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
                ? e.GetInt64() : 3600;

            return (access, refresh, expiresIn);
        }
        catch (JsonException)
        {
            return (null, null, 3600);
        }
    }

    // Atomically write the rotated token set back to .credentials.json, updating ONLY the
    // three fields and preserving every other field (scopes, subscriptionType, rateLimitTier,
    // any unknown keys, and any other top-level objects) byte-for-byte via JsonNode. Writes to
    // a temp file in the same directory, then move-with-overwrite so an interrupted write can
    // never leave a half-written/corrupt credentials file in place.
    private static async Task WriteCredentialsAsync(string accessToken, string refreshToken, long expiresAt)
    {
        var path = CredentialsPath;
        var json = await File.ReadAllTextAsync(path);

        if (JsonNode.Parse(json) is not JsonObject root)
            throw new InvalidOperationException("Credentials file is not a JSON object.");
        if (root["claudeAiOauth"] is not JsonObject oauth)
            throw new InvalidOperationException("claudeAiOauth object missing from credentials file.");

        oauth["accessToken"]  = accessToken;
        oauth["refreshToken"] = refreshToken;
        oauth["expiresAt"]    = expiresAt;

        var updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var dir  = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(dir, $".credentials.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(temp, updated);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort temp cleanup */ }
            throw;
        }
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

    private static async Task<Credentials> ReadCredentialsAsync()
    {
        var json = await File.ReadAllTextAsync(CredentialsPath);
        var creds = JsonSerializer.Deserialize<CredentialsFile>(json);
        var oauth = creds?.ClaudeAiOauth
            ?? throw new InvalidOperationException("claudeAiOauth not found in credentials file.");
        if (oauth.AccessToken is null)
            throw new InvalidOperationException("Access token not found in credentials file.");
        return new Credentials(oauth.AccessToken, oauth.RefreshToken, oauth.ExpiresAt);
    }

    // --- credentials models ---

    private sealed record Credentials(string AccessToken, string? RefreshToken, long ExpiresAt);

    private record CredentialsFile(
        [property: JsonPropertyName("claudeAiOauth")] OAuthCreds? ClaudeAiOauth);

    private record OAuthCreds(
        [property: JsonPropertyName("accessToken")]  string? AccessToken,
        [property: JsonPropertyName("refreshToken")] string? RefreshToken,
        [property: JsonPropertyName("expiresAt")]    long ExpiresAt);
}

// Thrown by FetchAsync when a poll could not be authorized AND our own network refresh is
// suppressed by the sticky token-endpoint 429 cooldown -- i.e. the token is expired, disk has
// nothing fresher to adopt, and we are deliberately NOT poking the rate-limited token endpoint.
// Distinct from a bare 401 so the poller can surface "token endpoint rate-limited -- waiting on
// host refresh" and keep polling at the normal cadence (each poll re-checks disk-adoption, the
// primary recovery path) instead of treating it as an ordinary auth failure. SuppressedUntilMs
// is the unix-ms instant the own-refresh suppression lifts.
public sealed class RefreshRateLimitedException : Exception
{
    public long SuppressedUntilMs { get; }

    public RefreshRateLimitedException(long suppressedUntilMs)
        : base("Token refresh suppressed by sticky 429 cooldown; awaiting host disk refresh.")
        => SuppressedUntilMs = suppressedUntilMs;
}
