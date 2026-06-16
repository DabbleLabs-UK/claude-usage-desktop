using System.Diagnostics;
using System.Text.Json;

namespace ClaudeUsage.Services;

// Refreshes the host Claude credentials by shelling out to the `claude` CLI.
//
// WHY: our own HTTP token-refresh against the OAuth token endpoint is ALWAYS 429'd (confirmed
// from both VM and host IPs -- the endpoint rejects our refresh regardless of IP), so the app
// cannot keep its own token fresh. The `claude` CLI, however, refreshes the on-disk token
// reliably. We drive it with `claude -p "hi"` -- a minimal one-shot prompt.
//
// WHY A PROMPT (not `auth status`): field logs PROVED `claude auth status` runs, reports
// loggedIn:true, exits 0, yet NEVER advances expiresAt -- it does not refresh. `claude auth login`
// refreshes but is interactive (opens a browser), so it's unusable headless. `claude -p "hi"`
// refreshes the on-disk token silently and reliably. The cost is tiny (a handful of tokens, a few
// times a day) and the app is transparent about it -- this behaviour is gated behind the
// autoRefreshLogin setting (default ON) with a plain-language explainer in the UI.
//
// All process work here is best-effort and fully guarded: a CLI fault must never disturb polling.
public sealed class ClaudeCli
{
    private readonly SettingsService _settings;
    private readonly AuthRefreshLog _authLog;

    private string? _resolvedPath;          // cached executable resolution
    private readonly object _resolveLock = new();

    public ClaudeCli(SettingsService settings, AuthRefreshLog authLog)
    {
        _settings = settings;
        _authLog  = authLog;
    }

    // Outcome of a `claude -p "hi"` invocation. Ran=false means we never launched the process
    // (not found / spawn failed); Ran=true with Error set means it launched but timed out/crashed.
    // LoggedIn is best-effort/informational only: the prompt reply is model text, not login JSON,
    // so it is usually false here. The REAL success signal is whether the on-disk token's expiresAt
    // advanced (judged by UsageService), never this field.
    public readonly record struct CliResult(bool Ran, int? ExitCode, bool LoggedIn, string? Error);

    // Resolves the claude executable robustly, caching the result. Order:
    //   1. settings.claudeCliPath override (if it exists on disk)
    //   2. `where claude` (searches PATH + PATHEXT -> claude.exe / claude.cmd)
    //   3. common install locations (~/.local/bin, %APPDATA%\npm)
    //   4. npm global prefix (`npm prefix -g`)
    // Returns null if nothing is found, so the caller can skip the CLI step cleanly. A cached path
    // that has since vanished is re-resolved.
    public string? ResolveExecutable()
    {
        lock (_resolveLock)
        {
            if (_resolvedPath is not null && File.Exists(_resolvedPath))
                return _resolvedPath;
            _resolvedPath = ResolveUncached();
            return _resolvedPath;
        }
    }

    private string? ResolveUncached()
    {
        // 1. explicit override
        var configured = _settings.Current.ClaudeCliPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            _authLog.Log($"CLI-RESOLVE: using configured claudeCliPath={configured}");
            return configured;
        }

        // 2. PATH via `where`
        var viaWhere = ResolveViaWhere();
        if (viaWhere is not null)
        {
            _authLog.Log($"CLI-RESOLVE: found on PATH (where) -> {viaWhere}");
            return viaWhere;
        }

        // 3. common install spots
        var home    = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] candidates =
        [
            Path.Combine(home,    ".local", "bin", "claude.exe"),
            Path.Combine(appdata, "npm", "claude.cmd"),
            Path.Combine(appdata, "npm", "claude.exe"),
        ];
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                _authLog.Log($"CLI-RESOLVE: found at common location -> {c}");
                return c;
            }
        }

        // 4. npm global prefix
        var viaNpm = ResolveViaNpmPrefix();
        if (viaNpm is not null)
        {
            _authLog.Log($"CLI-RESOLVE: found via npm prefix -> {viaNpm}");
            return viaNpm;
        }

        _authLog.Log("CLI-RESOLVE: claude executable NOT FOUND (override, PATH, common spots, npm prefix all empty).");
        return null;
    }

    private string? ResolveViaWhere()
    {
        var outp = RunCaptured("where.exe", "claude", timeoutMs: 5000);
        if (outp is null) return null;
        foreach (var line in outp.Split('\n'))
        {
            var path = line.Trim();
            if (path.Length > 0 && File.Exists(path))
                return path;
        }
        return null;
    }

    private string? ResolveViaNpmPrefix()
    {
        // npm is itself usually a .cmd on Windows, so go via cmd.exe.
        var prefix = RunCaptured("cmd.exe", "/c npm prefix -g", timeoutMs: 5000)?.Trim();
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        foreach (var name in new[] { "claude.cmd", "claude.exe" })
        {
            var cand = Path.Combine(prefix, name);
            if (File.Exists(cand)) return cand;
        }
        return null;
    }

    // Runs `claude -p "hi"` HIDDEN (no console flash) with a 30s timeout, capturing the exit
    // code + stdout. The prompt is deliberately minimal -- its only purpose is to make the CLI
    // refresh the near-expiry on-disk token as a side effect; the reply text is discarded. The
    // loggedIn parse is best-effort only (see CliResult). Never throws.
    public async Task<CliResult> RunRefreshPromptAsync()
    {
        var exe = ResolveExecutable();
        if (exe is null)
            return new CliResult(Ran: false, ExitCode: null, LoggedIn: false, Error: "not-found");

        try
        {
            var isScript = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                        || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            if (isScript)
            {
                // A .cmd/.bat shim can't be launched directly with redirect -- go via cmd.exe.
                psi.FileName  = "cmd.exe";
                psi.Arguments = $"/c \"\"{exe}\" -p \"hi\"\"";
            }
            else
            {
                psi.FileName  = exe;
                psi.Arguments = "-p \"hi\"";
            }

            using var p = new Process { StartInfo = psi };
            p.Start();

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new CliResult(Ran: true, ExitCode: null, LoggedIn: false, Error: "timeout-30s");
            }

            var stdout = await stdoutTask;
            await stderrTask;   // drain so the child can't block on a full stderr pipe

            return new CliResult(Ran: true, ExitCode: p.ExitCode, LoggedIn: ParseLoggedIn(stdout), Error: null);
        }
        catch (Exception ex)
        {
            return new CliResult(Ran: false, ExitCode: null, LoggedIn: false, Error: ex.GetType().Name);
        }
    }

    // Launches a short helper process and returns its stdout, or null on any failure/timeout.
    private static string? RunCaptured(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return outp;
        }
        catch
        {
            return null;
        }
    }

    // Tolerant: finds "loggedIn": true anywhere in the JSON. Falls back to a loose text check if
    // the output isn't JSON (older CLI builds). Used only for logging -- the real success signal
    // (in UsageService) is whether the on-disk token's expiresAt actually advanced.
    private static bool ParseLoggedIn(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return false;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return FindLoggedInTrue(doc.RootElement);
        }
        catch (JsonException)
        {
            return stdout.Contains("loggedIn", StringComparison.OrdinalIgnoreCase)
                && stdout.Contains("true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool FindLoggedInTrue(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.NameEquals("loggedIn") && prop.Value.ValueKind == JsonValueKind.True)
                        return true;
                    if (FindLoggedInTrue(prop.Value)) return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    if (FindLoggedInTrue(item)) return true;
                return false;
            default:
                return false;
        }
    }
}
