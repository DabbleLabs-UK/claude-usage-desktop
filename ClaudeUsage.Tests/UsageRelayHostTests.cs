using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

public class UsageRelayHostTests
{
    [Fact]
    public void RelayAuthorization_RequiresAnExactNonEmptyToken()
    {
        Assert.True(UsageRelayAuthorization.IsAuthorized("relay-secret", "relay-secret"));
        Assert.False(UsageRelayAuthorization.IsAuthorized("RELAY-SECRET", "relay-secret"));
        Assert.False(UsageRelayAuthorization.IsAuthorized("", "relay-secret"));
        Assert.False(UsageRelayAuthorization.IsAuthorized("relay-secret", ""));
    }
}
