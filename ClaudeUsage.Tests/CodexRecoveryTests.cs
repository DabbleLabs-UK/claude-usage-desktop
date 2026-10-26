using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

public class CodexRecoveryTests
{
    private static readonly CodexCredentialStore.CodexCreds SyntheticCredentials =
        new("synthetic-token", "synthetic-account", 0);

    [Fact]
    public async Task MissingAndExpiredLocalCredentials_DoNotMakeProviderRequests()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected HTTP request")));
        var missing = await CodexUsageService.FetchAsync(null, "synthetic-source", http);
        Assert.Equal(CodexState.NoToken, missing.State);
        Assert.Equal(CodexFailureReason.NoLocalCredentials, missing.Reason);
        Assert.Null(missing.HttpStatusCode);
        var expired = await CodexUsageService.FetchAsync(SyntheticCredentials with { ExpiresAtMs = 1 }, "synthetic-source", http);
        Assert.Equal(CodexState.SignedOut, expired.State);
        Assert.Equal(CodexFailureReason.LocalTokenExpired, expired.Reason);
        Assert.Null(expired.HttpStatusCode);
    }

    [Theory]
    [InlineData(401, CodexState.SignedOut, CodexFailureReason.UsageUnauthorized)]
    [InlineData(403, CodexState.SignedOut, CodexFailureReason.UsageForbidden)]
    [InlineData(429, CodexState.Error, null)]
    [InlineData(503, CodexState.Error, null)]
    public async Task RejectedUsage_ReportsOnlyObservedStatus_NotAnInferredSubscription(int code,
        CodexState state, CodexFailureReason? reason)
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(request => {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://chatgpt.com/backend-api/wham/usage", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization.Parameter);
            Assert.Equal("synthetic-account", Assert.Single(request.Headers.GetValues("ChatGPT-Account-Id")));
            return new HttpResponseMessage((HttpStatusCode)code) {
                Content = new StringContent("untrusted upstream body: subscription expired; do not expose")
            };
        }));
        var result = await CodexUsageService.FetchAsync(SyntheticCredentials, "private-store-path", http);
        Assert.Equal(1, requests); // No extra request, refresh or automatic retry inside the fetcher.
        Assert.Equal(state, result.State);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(code, result.HttpStatusCode);
        Assert.Null(result.Data);
        Assert.Null(result.AccountHash);
        Assert.Equal($"HTTP {code} from usage endpoint", result.Detail);
        Assert.DoesNotContain("subscription", result.Detail);
    }

    [Fact]
    public async Task RejectionThenSuccess_ClearsFailureAndRetainsExistingReadOnlyRequestPath()
    {
        var succeeds = false;
        using var http = new HttpClient(new Handler(_ => succeeds
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"rate_limit\":{\"primary_window\":{\"used_percent\":27,\"limit_window_seconds\":604800,\"reset_at\":1900000000}},\"credits\":{\"balance\":\"62500\"}}") }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var first = await CodexUsageService.FetchAsync(SyntheticCredentials, "synthetic-source", http);
        Assert.Equal(CodexFailureReason.UsageUnauthorized, first.Reason);
        succeeds = true;
        var recovered = await CodexUsageService.FetchAsync(SyntheticCredentials, "synthetic-source", http);
        Assert.Equal(CodexState.Live, recovered.State);
        Assert.Null(recovered.Reason);
        Assert.Null(recovered.HttpStatusCode);
        Assert.Equal(27, Assert.Single(recovered.Data!.Windows).UsedPercent);
        Assert.Equal(62500, recovered.Data.CreditsBalance);
        Assert.NotEqual("synthetic-account", recovered.AccountHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestFailure_RemainsIndependentNetworkState(bool timeout)
    {
        using var http = new HttpClient(new Handler(_ => throw (timeout
            ? new TaskCanceledException() : new HttpRequestException())));
        var result = await CodexUsageService.FetchAsync(SyntheticCredentials, "synthetic-source", http);
        Assert.Equal(CodexState.NoNetwork, result.State);
        Assert.Null(result.Reason);
        Assert.Null(result.HttpStatusCode);
    }

    [Fact]
    public void LastKnownState_IsRetainedOnlyForThisProcess_AndRecoveryClearsStaleness()
    {
        var at = DateTimeOffset.Parse("2026-10-26T13:02:53Z");
        var good = new CodexUsageData([new(27, 604800, at.AddDays(1))], 62500, at);
        var state = new CodexUsageState();
        state.Update(good);
        state.SetStatus(new(CodexState.SignedOut, CodexFailureReason.UsageUnauthorized, 401));
        state.MarkStale();
        Assert.True(state.Current!.IsStale);
        Assert.Equal(at, state.Current.FetchedAt);
        Assert.Equal(good.Windows, state.Current.Windows);
        Assert.Equal(62500, state.Current.CreditsBalance);
        Assert.Null(new CodexUsageState().Current); // No invented disk restoration of quota/credit snapshots.
        state.SetStatus(new(CodexState.Live));
        state.Update(good with { FetchedAt = at.AddMinutes(40) });
        Assert.False(state.Current!.IsStale);
        Assert.Null(state.Status!.Reason);
        Assert.Null(state.Status.HttpStatusCode);
    }

    [Fact]
    public void BrowserStatus_ContainsSafeObservationsOnly()
    {
        var at = DateTimeOffset.Parse("2026-10-26T13:39:04Z");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var status = new CodexStatus(CodexState.SignedOut, CodexFailureReason.UsageUnauthorized, 401,
            at, at.AddSeconds(180));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status, options));
        var root = json.RootElement;
        Assert.Equal("signedOut", root.GetProperty("state").GetString());
        Assert.Equal("usageUnauthorized", root.GetProperty("reason").GetString());
        Assert.Equal(401, root.GetProperty("httpStatusCode").GetInt32());
        Assert.Equal(at, root.GetProperty("checkedAt").GetDateTimeOffset());
        Assert.Equal(at.AddSeconds(180), root.GetProperty("nextAttemptAt").GetDateTimeOffset());
        Assert.Equal(new[] { "checkedAt", "httpStatusCode", "nextAttemptAt", "reason", "state" },
            root.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
