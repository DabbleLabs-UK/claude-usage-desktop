using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeUsage.Services;

// Headless diagnostic (run: ClaudeUsage.exe --auth-probe). Resolves the credential source the poller
// would use, prints a redacted summary, then performs ONE live usage poll -- so auth wiring can be
// verified on a machine without launching the full WPF app (used to confirm the Windows Credential
// Manager path on the host). Output goes to the parent console (when attached) AND to a temp file,
// since a WinExe launched from some terminals has no inherited console.
internal static class AuthProbe
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    public static int Run()
    {
        AttachConsole(ATTACH_PARENT_PROCESS);

        var sb = new StringBuilder();
        int exit;
        try
        {
            var settings = new SettingsService();
            var (creds, source) = UsageService.ResolveCredentialsAsync(settings).GetAwaiter().GetResult();
            var t = creds.AccessToken;
            var preview = t.Length > 14 ? $"{t[..10]}...({t.Length} chars)" : $"({t.Length} chars)";

            sb.AppendLine($"[auth-probe] token source : {source}");
            sb.AppendLine($"[auth-probe] access token : {preview}");
            sb.AppendLine($"[auth-probe] refresh token: {(creds.RefreshToken is null ? "none" : "present")}");
            sb.AppendLine($"[auth-probe] expiresAt    : {(creds.ExpiresAt == 0 ? "unknown" : DateTimeOffset.FromUnixTimeMilliseconds(creds.ExpiresAt).ToString("u"))}");

            try
            {
                _ = UsageService.PollUsageAsync(t).GetAwaiter().GetResult();
                sb.AppendLine("[auth-probe] usage poll  : HTTP 200 OK -- live data received.");
                exit = 0;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[auth-probe] usage poll  : FAILED -- {ex.Message}");
                exit = 2;
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[auth-probe] credential resolution FAILED -- {ex.Message}");
            exit = 3;
        }

        var report = sb.ToString();
        Console.Write(report);
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "claude-usage-auth-probe.txt");
            File.WriteAllText(path, report);
            Console.WriteLine($"[auth-probe] (report also written to {path})");
        }
        catch { }
        return exit;
    }
}
