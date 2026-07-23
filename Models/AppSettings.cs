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
    string? ClaudeCliPath = null,
    // Manual override for the usage bearer token (env CLAUDE_CODE_OAUTH_TOKEN takes precedence over
    // this). Escape hatch only -- normally the token is read from the Windows Credential Manager
    // (where Claude Code 2.1.x stores the live OAuth login) or, on older CC, ~/.claude/.credentials
    // .json; both are found automatically. IMPORTANT: this must be a REAL OAuth access token -- a
    // `claude setup-token` value does NOT work (it is inference-scoped; the usage endpoint returns
    // 403). Not a UI field -- edited directly in settings.json (held in %APPDATA%\ClaudeUsage).
    string? ClaudeCodeOauthToken = null,
    // When OFF (default), windows the API reports as "not tracked this period" (null, e.g.
    // SONNET (7-DAY) / OPUS (7-DAY) on a plan that doesn't split them) are hidden from the home
    // view behind a small "show N not tracked" toggle, instead of rendering permanently greyed.
    // A window that later returns real data reappears automatically regardless of this flag.
    // Toggled via the inline reveal link (POST /api/untracked-windows).
    bool ShowUntrackedWindows = false);

// Body for POST /api/hints/dismiss — the id of the hint card being dismissed.
public record HintDismissRequest(string Id);

// Body for POST /api/untracked-windows — persists the "show untracked windows" reveal toggle.
public record UntrackedWindowsRequest(bool Show);

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
