using Xunit;

namespace ClaudeUsage.Tests;

public class FrontendContractTests
{
    private static string IndexHtmlPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "wwwroot", "index.html"));

    [Fact]
    public void AgentSpendStaleCard_DoesNotReuseHiddenToolbarBadgeClass()
    {
        var html = File.ReadAllText(IndexHtmlPath);

        Assert.Contains(".agent-spend-card.agent-spend-stale", html);
        Assert.Contains("data.isStale ? ' agent-spend-stale' : ''", html);
        Assert.DoesNotContain(".agent-spend-card.stale", html);
        Assert.DoesNotContain("data.isStale ? ' stale' : ''", html);
    }

    [Fact]
    public void AgentSpendCard_IdentifiesLauncherOnlyScope()
    {
        var html = File.ReadAllText(IndexHtmlPath);

        Assert.Contains("Launcher Spend", html);
        Assert.Contains("not your complete OpenRouter account", html);
        Assert.DoesNotContain("<span class=\"label\">API Spend</span>", html);
    }
}
