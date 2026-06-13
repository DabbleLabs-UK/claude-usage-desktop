namespace ClaudeUsage.Services;

// Shared per-category teardown primitives used by BOTH the in-app Reset/clear-data feature
// (POST /api/reset in App.xaml.cs) AND the headless `--uninstall-cleanup` CLI path
// (UninstallCleanup). Keeping the actual file/dir removal here means the two callers can't
// drift: the reset UI and the uninstaller delete exactly the same things, the same way.
// (Firewall removal lives in FirewallService.TryRemoveRuleAsync and autostart removal in
// SettingsService.RemoveAutostart -- both callers reuse those, for the same no-drift reason.)
public static class Teardown
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsage");

    // The WebView2 user-data folder. WebView2 defaults it to "<exe-filename>.WebView2" beside the
    // executable (e.g. dist\ClaudeUsage.exe.WebView2\). It is locked for the life of the running
    // process; it deletes cleanly only once the app has exited (the uninstaller's case), or via the
    // detached exit-helper for the in-app reset.
    public static string? WebView2CacheDir()
    {
        var exe = Environment.ProcessPath;
        var dir = exe is null ? null : Path.GetDirectoryName(exe);
        return dir is null ? null : Path.Combine(dir, Path.GetFileName(exe!) + ".WebView2");
    }

    // Delete one file under %APPDATA%\ClaudeUsage. Returns true if the file is gone afterwards
    // (deleted, or never existed); false only if a delete was attempted and threw (why = exception
    // type name). A held/locked file is the only realistic failure.
    public static bool DeleteAppDataFile(string name, out string why)
    {
        why = "";
        try
        {
            var path = Path.Combine(DataDir, name);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) { why = ex.GetType().Name; return false; }
    }

    // Delete one subdirectory under %APPDATA%\ClaudeUsage, recursively. Returns true if gone
    // afterwards (deleted, or never existed); false if a delete was attempted and threw.
    public static bool DeleteAppDataSubdir(string name, out string why)
    {
        why = "";
        try
        {
            var path = Path.Combine(DataDir, name);
            if (Directory.Exists(path)) Directory.Delete(path, true);
            return true;
        }
        catch (Exception ex) { why = ex.GetType().Name; return false; }
    }

    // Delete the WebView2 cache directory (beside the exe). Returns true if gone afterwards;
    // false if it exists but a delete threw (e.g. the app is still running and holds it).
    public static bool DeleteWebView2Cache(out string why)
    {
        why = "";
        var dir = WebView2CacheDir();
        try
        {
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
            return true;
        }
        catch (Exception ex) { why = ex.GetType().Name; return false; }
    }
}
