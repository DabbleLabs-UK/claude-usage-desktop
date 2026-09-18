namespace ClaudeUsage.Services;

// The relay token is a local access-control secret, not a Claude OAuth token.
// Keep the exact-match rule dependency-free so it is independently testable.
public static class UsageRelayAuthorization
{
    public static bool IsAuthorized(string suppliedToken, string expectedToken) =>
        !string.IsNullOrWhiteSpace(expectedToken) &&
        string.Equals(suppliedToken, expectedToken, StringComparison.Ordinal);
}
