using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

public class UsageResilienceTests
{
    private static UsageData Sample(DateTimeOffset? at = null) => new(
        new Dictionary<string, UsageWindow?> { ["five_hour"] = new(16, null) }, null,
        at ?? DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(ConnectivityState.SignedOut)]
    [InlineData(ConnectivityState.RateLimited)]
    [InlineData(ConnectivityState.NoNetwork)]
    [InlineData(ConnectivityState.SourcesUnavailable)]
    [InlineData(ConnectivityState.AuthError)]
    public void FailureThenRecovery_PreservesReadingAndClearsEveryWarning(ConnectivityState failure)
    {
        var state = new UsageState();
        var good = Sample();
        state.Update(good);
        state.SetBackoff(new(failure, 0, 30, DateTimeOffset.UtcNow.AddSeconds(30)));
        Assert.True(state.Current!.IsStale);
        Assert.Equal(good.FetchedAt, state.Current.FetchedAt);
        state.Update(good with { Source = "Work PC" });
        Assert.Null(state.Backoff);
        Assert.False(state.Current!.IsStale);
    }

    [Fact]
    public void RelayCache_ExpiresAndInvalidatesFailedReading_WithoutRenewingTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new RelaySnapshotStore();
        Assert.Null(store.Read(now));
        var good = Sample(now);
        store.Success(good);
        Assert.Equal(good, store.Read(now.AddMinutes(9)));
        Assert.Null(store.Read(now.AddMinutes(10)));
        store.Failure();
        Assert.Null(store.Read(now));
        store.Success(Sample(now.AddSeconds(1)));
        Assert.NotNull(store.Read(now.AddSeconds(1)));
    }

    [Fact]
    public async Task RelayClient_RejectsBadSnapshots_AndRecoversWithoutRestart()
    {
        var now = DateTimeOffset.UtcNow;
        UsageData? response = Sample(now) with { IsStale = true };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(response) })));
        var client = new UsageRelayClient(http, () => [new("Work PC", "http://work/v1/usage", "private-key")]);
        foreach (var invalid in new UsageData?[] { response, Sample(now.AddMinutes(-11)),
            Sample(now.AddMinutes(2)), Sample(now) with { Windows = null! },
            Sample(now) with { Windows = new Dictionary<string, UsageWindow?>() }, null })
        {
            response = invalid;
            Assert.Null(await client.TryFetchFreshAsync(default));
        }
        response = Sample();
        var recovered = await client.TryFetchFreshAsync(default);
        Assert.Equal("Work PC", recovered!.Source);
        Assert.False(recovered.IsStale);
        Assert.Equal("", client.LastFailure);
    }

    [Fact]
    public async Task RelayClient_ReportsConnectionKeyErrors_WithoutLeakingSecrets()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        var client = new UsageRelayClient(http, () => [new("Work PC", "http://private-address/v1/usage", "secret")]);
        Assert.Null(await client.TryFetchFreshAsync(default));
        Assert.Contains("access key rejected", client.LastFailure);
        Assert.DoesNotContain("secret", client.LastFailure);
        Assert.DoesNotContain("private-address", client.LastFailure);
    }

    [Fact]
    public async Task RelayClient_UsesNewConfiguration_AndNewestHealthyPeer()
    {
        var now = DateTimeOffset.UtcNow;
        UsageRelaySource[] config = [];
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                Sample(request.RequestUri!.Host == "older" ? now.AddMinutes(-1) : now)) })));
        var client = new UsageRelayClient(http, () => config);
        Assert.Null(await client.TryFetchFreshAsync(default));
        config = [new("Old", "http://older/v1/usage", "key"), new("New", "http://newer/v1/usage", "key")];
        Assert.Equal("New", (await client.TryFetchFreshAsync(default))!.Source);
    }

    [Fact]
    public async Task RelayClient_PropagatesShutdownCancellation()
    {
        using var http = new HttpClient(new Handler(async (_, ct) => {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var client = new UsageRelayClient(http, () => [new("PC", "http://pc/v1/usage", "key")]);
        using var cancel = new CancellationTokenSource();
        var pending = client.TryFetchFreshAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void BrowserSettings_DoNotExposeCredentials_OrMutateOriginal()
    {
        var original = new AppSettings(ClaudeCodeOauthToken: "claude-secret",
            UsageRelaySources: [new("PC", "http://pc/v1/usage", "relay-secret")]);
        var json = JsonSerializer.Serialize(SettingsPrivacy.Redact(original));
        Assert.DoesNotContain("claude-secret", json);
        Assert.DoesNotContain("relay-secret", json);
        Assert.Equal("relay-secret", original.UsageRelaySources![0].AccessToken);
    }

    [Fact]
    public async Task RemoteFailureAndLocalFailure_ReportsSourceOutage_ThenRecovers()
    {
        var available = false;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(available
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Sample()) }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var relay = new UsageRelayClient(http, () => [new("Office", "http://office/v1/usage", "key")]);
        Func<Task<UsageData>> failedLocal = () => throw new InvalidOperationException("expired local token");
        var failure = await Assert.ThrowsAsync<UsageSourcesUnavailableException>(
            () => UsageSourceSelector.FetchAsync(relay, failedLocal, default));
        Assert.Contains("Office", failure.Message);
        Assert.DoesNotContain("expired local token", failure.Message);
        var fallback = await UsageSourceSelector.FetchAsync(relay, () => Task.FromResult(Sample()), default);
        Assert.Contains("This PC", fallback.Source);
        available = true;
        var recovered = await UsageSourceSelector.FetchAsync(relay, failedLocal, default);
        Assert.Equal("Office", recovered.Source);
    }

    [Fact]
    public async Task LocalOnly_PreservesActualErrorType()
    {
        using var http = new HttpClient();
        var relay = new UsageRelayClient(http, () => []);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => UsageSourceSelector.FetchAsync(relay,
            () => throw new UnauthorizedAccessException(), default));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
