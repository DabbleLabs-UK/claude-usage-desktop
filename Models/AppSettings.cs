namespace ClaudeUsage.Models;

public record AppSettings(
    bool StartWithWindows = false,
    int ServerPort = 5005,
    bool CloseToTray = true,
    bool FirewallSetupDone = false,
    bool FirewallDeclined = false,
    double OrangeThreshold = 0.25,
    double RedThreshold = 0.10,
    double YellowThreshold = 0.50,
    double YellowGreenThreshold = 0.75);
