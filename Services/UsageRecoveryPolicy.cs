using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// A successful poll must clear every previously surfaced connectivity state. Some waiting states
// (notably SignedOut) intentionally do not increment the failure counter, so the counter alone is
// not sufficient evidence that a banner is active.
public static class UsageRecoveryPolicy
{
    public static bool ShouldClearBackoff(int failureCount, BackoffInfo? backoff) =>
        failureCount > 0 || backoff is not null;
}
