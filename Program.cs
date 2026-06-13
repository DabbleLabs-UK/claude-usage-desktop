using System.Windows;

namespace ClaudeUsage;

// Explicit entry point (selected via <StartupObject> in the .csproj, overriding the Main that
// WPF's ApplicationDefinition would otherwise generate). This lets us intercept the headless
// --uninstall-cleanup flag BEFORE the WPF Application is constructed, so that path never starts
// Kestrel, never creates a tray icon and never shows a window -- it does the teardown and exits
// with a clear code. Every other launch falls through to the normal WPF startup.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (HasFlag(args, "--uninstall-cleanup"))
            return UninstallCleanup.Run(purgeUsageLogs: HasFlag(args, "--purge-usage-logs"));

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static bool HasFlag(string[] args, string flag) =>
        Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}
