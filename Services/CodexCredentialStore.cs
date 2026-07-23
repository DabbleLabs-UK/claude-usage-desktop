using System.Text.Json;

namespace ClaudeUsage.Services;

// Resolves Codex's cached OAuth credential for READ-ONLY usage polling. Codex (OpenAI's CLI)
// owns this credential and its refresh entirely; we NEVER write it back and NEVER refresh --
// racing the CLI's token rotation could corrupt the user's login. We only read.
//
// Resolution mirrors the Claude side's style:
//   1. Windows Credential Manager -- when the user sets cli_auth_credentials_store to a keyring,
//      Codex stores the SAME auth.json content there (content-matched; see
//      WindowsCredentialStore.TryReadCodexAuthJson).
//   2. CODEX_HOME/auth.json, else ~/.codex/auth.json -- the default on-disk store.
// Returns the credential, or null when no Codex login exists at all (the caller then hides the
// whole Codex section rather than showing an empty placeholder).
internal static class CodexCredentialStore
{
    // ExpiresAtMs is best-effort, decoded from the access_token JWT's exp claim (0 = unknown /
    // opaque token). It lets the service surface an honest "signed out" WITHOUT calling or
    // refreshing when the cached token is already expired.
    public sealed record CodexCreds(string AccessToken, string? AccountId, long ExpiresAtMs);

    public static CodexCreds? Resolve(out string source)
    {
        source = "none";

        // 1. Windows Credential Manager (cli_auth_credentials_store -> OS keyring), content-matched.
        var storeJson = WindowsCredentialStore.TryReadCodexAuthJson(out var target);
        if (storeJson is not null && TryParse(storeJson, out var storeCreds))
        {
            source = $"Windows Credential Manager [{target}]";
            return storeCreds;
        }

        // 2. CODEX_HOME/auth.json, else ~/.codex/auth.json.
        var path = AuthPath;
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                if (TryParse(json, out var fileCreds))
                {
                    source = $"auth.json [{path}]";
                    return fileCreds;
                }
            }
            catch
            {
                // Unreadable / locked mid-rotation by the Codex CLI -> treat as "no token this
                // cycle"; the next poll re-reads. We never lock or write the file ourselves.
            }
        }

        return null;
    }

    private static string CodexHome
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }
    }

    private static string AuthPath => Path.Combine(CodexHome, "auth.json");

    // auth.json's exact shape isn't fully documented -- tolerate tokens.* or top-level (exactly
    // as the feasibility probe did). Requires an access_token; account_id is best-effort (falls
    // back to the id_token's chatgpt_account_id claim).
    private static bool TryParse(string json, out CodexCreds creds)
    {
        creds = null!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var scope = root.TryGetProperty("tokens", out var tokens)
                        && tokens.ValueKind == JsonValueKind.Object
                ? tokens : root;

            var access = GetStr(scope, "access_token") ?? GetStr(root, "access_token");
            if (string.IsNullOrWhiteSpace(access)) return false;

            var accountId = GetStr(scope, "account_id") ?? GetStr(root, "account_id");
            if (string.IsNullOrWhiteSpace(accountId))
                accountId = AccountIdFromIdToken(GetStr(scope, "id_token") ?? GetStr(root, "id_token"));

            creds = new CodexCreds(access!, string.IsNullOrWhiteSpace(accountId) ? null : accountId, JwtExpiryMs(access!));
            return true;
        }
        catch { return false; }
    }

    private static string? GetStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // Best-effort account id from the id_token JWT (OpenAI nests it under a namespaced auth claim).
    private static string? AccountIdFromIdToken(string? idToken)
    {
        var claims = JwtClaims(idToken);
        if (claims is null) return null;
        try
        {
            if (claims.Value.TryGetProperty("https://api.openai.com/auth", out var auth)
                && auth.ValueKind == JsonValueKind.Object
                && auth.TryGetProperty("chatgpt_account_id", out var acc)
                && acc.ValueKind == JsonValueKind.String)
                return acc.GetString();
        }
        catch { }
        return null;
    }

    // Best-effort access_token expiry (unix-ms) from its JWT exp claim; 0 when not a decodable JWT.
    private static long JwtExpiryMs(string token)
    {
        var claims = JwtClaims(token);
        if (claims is null) return 0;
        try
        {
            if (claims.Value.TryGetProperty("exp", out var exp) && exp.ValueKind == JsonValueKind.Number)
                return exp.GetInt64() * 1000;
        }
        catch { }
        return 0;
    }

    private static JsonElement? JwtClaims(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
