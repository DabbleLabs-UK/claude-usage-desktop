namespace ClaudeUsage.Services;

// Pure decision policy for the API-spend lane, extracted from AgentSpendPoller so the "adopt vs.
// retain-last-good" rule is unit-testable without any hosting/hub/file-system machinery (mirrors
// LoginStatePolicy / RefreshCooldownPolicy elsewhere in this project).
//
// The contract says: on a missing file, malformed JSON, or source.status != "ok", retain the last
// good snapshot and mark it stale rather than adopting the new (untrustworthy) read. ShouldAdopt
// is that gate. IsFreshEnough is a second, independent check applied to the local file's update
// time. A successful Windows sync rewrites the file every five minutes even when no launcher runs
// occurred, so this detects a broken sync without incorrectly treating an idle launcher as stale.
public static class AgentSpendFreshnessPolicy
{
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(30);

    public static bool ShouldAdopt(string sourceStatus) =>
        string.Equals(sourceStatus, "ok", StringComparison.Ordinal);

    public static bool IsFreshEnough(DateTimeOffset fileUpdatedAt, DateTimeOffset now) =>
        now - fileUpdatedAt <= FreshWindow;
}
