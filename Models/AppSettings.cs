namespace ClaudeUsage.Models;

public record AppSettings(
    bool StartWithWindows = false,
    int ServerPort = 5005,
    bool CloseToTray = true,
    bool FirewallSetupDone = false,
    bool FirewallDeclined = false);
