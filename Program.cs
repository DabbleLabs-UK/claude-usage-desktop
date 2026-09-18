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

        // Headless auth diagnostic: print the resolved credential source + one live usage poll, then
        // exit (never starts the WPF app). Used to verify the credential wiring on a given machine.
        if (HasFlag(args, "--auth-probe"))
            return ClaudeUsage.Services.AuthProbe.Run();

        if (HasFlag(args, "--relay"))
        {
            var port = ReadIntOption(args, "--relay-port", 5055);
            var token = ReadOption(args, "--relay-token");
            return ClaudeUsage.Services.UsageRelayHost.RunAsync(port, token ?? "").GetAwaiter().GetResult();
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static bool HasFlag(string[] args, string flag) =>
        Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? ReadOption(string[] args, string option)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static int ReadIntOption(string[] args, string option, int fallback) =>
        int.TryParse(ReadOption(args, option), out var value) ? value : fallback;
}
