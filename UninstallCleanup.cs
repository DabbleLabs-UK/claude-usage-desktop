using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ClaudeUsage.Services;

namespace ClaudeUsage;

// Headless teardown invoked as:  ClaudeUsage.exe --uninstall-cleanup [--purge-usage-logs]
//
// Designed to be driven from an Inno Setup [UninstallRun] step. Runs with NO window, NO Kestrel
// server and NO tray icon -- Program.Main intercepts the flag before the WPF App is ever created,
// does the work, and returns an exit code.
//
// Removes: the firewall rule (configured port), the HKCU Run autostart entry, and the
// %APPDATA%\ClaudeUsage data (settings.json, the diagnostic logs, and the WebView2 cache beside
// the exe). USAGE LOGS ARE PRESERVED BY DEFAULT -- the usage-log\ folder is only deleted when
// --purge-usage-logs is also passed (mirrors the in-app reset dialog's "usage logs off by default").
//
// Reuses the SAME per-category teardown the in-app reset uses -- FirewallService.TryRemoveRuleAsync,
// SettingsService.RemoveAutostart, and Teardown.* -- so the uninstaller and the reset UI can't drift.
public static class UninstallCleanup
{
    // Exit codes -- an Inno [UninstallRun] can branch on these:
    //   0  success: everything requested was removed.
    //   2  partial: data + autostart cleaned, but the firewall rule could not be removed
    //               (no admin rights / UAC declined). Safe for the uninstaller to continue.
    //   1  unexpected fatal error before/while running the teardown.
    public const int ExitSuccess = 0;
    public const int ExitError = 1;
    public const int ExitFirewallNotRemoved = 2;

    public static int Run(bool purgeUsageLogs)
    {
        var line = new StringBuilder();
        void Emit(string s) { line.AppendLine(s); }

        var removed = new List<string>();
        var kept = new List<string>();
        var failed = new List<string>();
        int exit = ExitSuccess;

        try
        {
            // Read the configured port from settings.json BEFORE we delete it, so we remove the
            // right firewall rule (rule name is "ClaudeUsage-{port}"). Falls back to the default.
            int port;
            try { port = new SettingsService().Current.ServerPort; }
            catch { port = new Models.AppSettings().ServerPort; }

            Emit($"ClaudeUsage --uninstall-cleanup  (elevated={IsElevated()}, port={port}, purgeUsageLogs={purgeUsageLogs})");

            // 1. Firewall rule -- reuse the app's own UAC-elevated removal. Under an Inno elevated
            //    uninstaller this child already holds the admin token, so runas raises no prompt.
            var fw = new FirewallService();
            var fwResult = fw.TryRemoveRuleAsync(port).GetAwaiter().GetResult();
            switch (fwResult)
            {
                case FirewallResult.Added: // gone (removed, or was already absent)
                    removed.Add($"Firewall rule (ClaudeUsage-{port})");
                    break;
                case FirewallResult.Declined:
                    failed.Add("Firewall rule -- UAC declined (needs admin)");
                    exit = ExitFirewallNotRemoved;
                    break;
                default:
                    failed.Add("Firewall rule -- removal failed");
                    exit = ExitFirewallNotRemoved;
                    break;
            }

            // 2. Autostart (HKCU Run) -- shared single source of truth with the reset path.
            SettingsService.RemoveAutostart();
            removed.Add("Autostart entry (HKCU\\...\\Run\\ClaudeUsage)");

            // 3. settings.json + diagnostic logs.
            foreach (var f in new[] { "settings.json", "auth-refresh.log", "startup-error.log" })
            {
                if (Teardown.DeleteAppDataFile(f, out var why)) removed.Add($"{f}");
                else failed.Add($"{f} -- {why}");
            }

            // 4. WebView2 cache (beside the exe). Should delete cleanly: the GUI is not running in
            //    this headless invocation, so nothing holds the folder.
            if (Teardown.DeleteWebView2Cache(out var wvWhy)) removed.Add("WebView2 cache");
            else failed.Add($"WebView2 cache -- {wvWhy}");

            // 5. Usage logs -- PROTECTED by default; only purged with the explicit extra flag.
            if (purgeUsageLogs)
            {
                if (Teardown.DeleteAppDataSubdir("usage-log", out var why)) removed.Add("Usage logs (usage-log\\)");
                else failed.Add($"Usage logs -- {why}");
            }
            else
            {
                kept.Add("Usage logs (usage-log\\) -- preserved; pass --purge-usage-logs to delete");
            }

            // 6. Remove the now-empty %APPDATA%\ClaudeUsage dir, but ONLY if nothing remains (i.e.
            //    usage logs were purged and no stray files are left). When logs are preserved the
            //    folder is intentionally left in place.
            try
            {
                if (Directory.Exists(Teardown.DataDir) &&
                    !Directory.EnumerateFileSystemEntries(Teardown.DataDir).Any())
                {
                    Directory.Delete(Teardown.DataDir, false);
                    removed.Add("Empty %APPDATA%\\ClaudeUsage folder");
                }
            }
            catch { /* leaving an empty folder behind is harmless */ }
        }
        catch (Exception ex)
        {
            Emit($"FATAL: {ex.GetType().Name}: {ex.Message}");
            exit = ExitError;
        }

        // Summary -- to a durable log (NOT under %APPDATA%, which we may have just deleted) and to
        // the parent console if one is attached (e.g. run manually from a terminal).
        Emit("Removed:");
        foreach (var r in removed) Emit($"  + {r}");
        if (kept.Count > 0) { Emit("Kept:"); foreach (var k in kept) Emit($"  = {k}"); }
        if (failed.Count > 0) { Emit("Could NOT remove:"); foreach (var f in failed) Emit($"  ! {f}"); }
        Emit($"Exit code: {exit}");

        WriteAudit(line.ToString());
        return exit;
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // Write the audit summary to %TEMP%\ClaudeUsage-uninstall-cleanup.log (survives the data wipe)
    // and, if this process was launched from a console, echo it there too.
    private static void WriteAudit(string text)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), "ClaudeUsage-uninstall-cleanup.log");
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:o}\n{text}\n");
        }
        catch { /* best-effort */ }

        try
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                Console.Out.Write(text);
                Console.Out.Flush();
            }
        }
        catch { /* no parent console (e.g. launched by the uninstaller): the log file is the record */ }
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
