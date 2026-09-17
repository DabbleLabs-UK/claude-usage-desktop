namespace ClaudeUsage.Services;

// Pure decision policy for the API-spend lane, extracted from AgentSpendPoller so the "adopt vs.
// retain-last-good" rule is unit-testable without any hosting/hub/file-system machinery (mirrors
// LoginStatePolicy / RefreshCooldownPolicy elsewhere in this project).
//
// The contract says: on a missing file, malformed JSON, or source.status != "ok", retain the last
// good snapshot and mark it stale rather than adopting the new (untrustworthy) read. ShouldAdopt
// is that gate. IsFreshEnough is a second, independent check applied even to an adopted read: a
// producer that keeps writing status "ok" but has stopped actually updating (a hung background
// job) would otherwise look perpetually live. There is no producer cadence stated in the contract,
// so FreshWindow is a conservative guess, not a value read from the file.
public static class AgentSpendFreshnessPolicy
{
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(30);

    public static bool ShouldAdopt(string sourceStatus) =>
        string.Equals(sourceStatus, "ok", StringComparison.Ordinal);

    public static bool IsFreshEnough(DateTimeOffset generatedAt, DateTimeOffset now) =>
        now - generatedAt <= FreshWindow;
}
