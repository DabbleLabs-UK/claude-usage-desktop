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
    public void OpenRouterSpendCard_IdentifiesAccountScopeAndSeparateFeed()
    {
        var html = File.ReadAllText(IndexHtmlPath);

        Assert.Contains("OpenRouter Account Spend", html);
        Assert.Contains("across all API keys in the connected account", html);
        Assert.Contains("/api/openrouter-spend", html);
        Assert.DoesNotContain("/api/agent-spend", html);
        Assert.DoesNotContain("Launcher Spend", html);
        Assert.DoesNotContain("<span class=\"label\">API Spend</span>", html);
    }

    [Fact]
    public void SharedStatusAsset_IsIncludedInExistingVersionedAssetDelivery()
    {
        var appSource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(
            Path.GetDirectoryName(IndexHtmlPath))!, "App.xaml.cs"));
        Assert.Contains("\"provider-status.js\", \"usage-stats.js\"", appSource);
        Assert.Contains("html.Replace($\"\\\"{asset}\\\"\", $\"\\\"{asset}?v={assetVer}\\\"\")", appSource);
    }
}
