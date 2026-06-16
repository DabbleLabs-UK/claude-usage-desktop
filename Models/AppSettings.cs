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
    double YellowGreenThreshold = 0.75,
    string? PreferredAdapterName = null,
    // True once the guided phone-access onboarding flow has auto-shown on first run.
    bool SeenPhoneOnboarding = false,
    // Ids of dismissible hint cards the user has permanently dismissed (e.g. "phone-access").
    string[]? DismissedHints = null,
    // When ON (default), the app keeps the host Claude login fresh automatically: as the on-disk
    // token nears expiry it shells out to the `claude` CLI for a tiny prompt (a handful of tokens,
    // a few times a day) so usage keeps showing without a manual re-sign-in. When OFF, the app
    // never shells out to `claude` -- it shows the stale state and the user refreshes manually.
    bool AutoRefreshLogin = true,
    // Optional manual override for the `claude` CLI executable used to refresh the host token
    // (see ClaudeCli). Normally auto-detected (PATH / common install spots); set this only when
    // resolution fails. Not a UI field -- edited directly in settings.json.
    string? ClaudeCliPath = null);

// Body for POST /api/hints/dismiss — the id of the hint card being dismissed.
public record HintDismissRequest(string Id);

// Body for POST /api/reset — independent flags for the selective clear-data feature. Only the
// ticked categories are cleared; all-false is a no-op. Restart=false (default) closes the app.
public record ResetRequest(
    bool Settings = false,
    bool Onboarding = false,
    bool WebView2 = false,
    bool UsageLogs = false,
    bool Firewall = false,
    bool Autostart = false,
    bool Restart = false);
