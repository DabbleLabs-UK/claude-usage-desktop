using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

public class OpenRouterSpendTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T16:00:00Z");
    private const string AccountA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AccountB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ValidBody = """
    {
      "schema_version": 1,
      "scope": "openrouter_account",
      "account_scope_id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "generated_at": "2026-10-10T16:00:00Z",
      "currency": "USD",
      "timezone": "UTC",
      "source": { "status": "ok", "last_success_at": "2026-10-10T16:00:00Z" },
      "rolling_24h": { "total_cost": 1.5, "priced_runs": 3, "unpriced_runs": 0 },
      "rolling_7d": { "total_cost": 7, "daily_average": 1, "priced_runs": 14, "unpriced_runs": 0 },
      "month_to_date": { "total_cost": 10, "priced_runs": 20, "unpriced_runs": 0 },
      "run_rates": { "monthly_at_24h_pace": 45, "monthly_at_7d_pace": 30 },
      "calendar_week": {
        "total_cost": 6, "priced_runs": 12, "unpriced_runs": 0, "starts_at": "2026-10-05T00:00:00Z"
      },
      "breakdown_by_model": [
        { "model": "model-a", "provider": "openrouter", "cost": 7, "priced_runs": 14,
          "unpriced_runs": 0, "input_tokens": 1000, "output_tokens": 500 }
      ],
      "breakdown_by_provider": [
        { "provider": "openrouter", "cost": 7, "priced_runs": 14, "unpriced_runs": 0 }
      ],
      "daily_history": [
        { "date": "2026-10-09", "cost": 1, "priced_runs": 2, "unpriced_runs": 0 }
      ]
    }
    """;

    private static JsonObject Body() => JsonNode.Parse(ValidBody)!.AsObject();

    private static OpenRouterSpendReader.ReadResult Parse(JsonObject body) =>
        OpenRouterSpendReader.Parse(body.ToJsonString());

    private static OpenRouterSpendState LiveState()
    {
        var state = new OpenRouterSpendState();
        state.Apply(OpenRouterSpendReader.Parse(ValidBody), Now);
        return state;
    }

    private static string ErrorMarker(string identity) => JsonSerializer.Serialize(new
    {
        schema_version = 1,
        scope = "openrouter_account",
        account_scope_id = identity,
        source = new { status = "error", last_success_at = (string?)null }
    });

    [Fact]
    public void CompleteSnapshot_UsesPublishedAmountsAndCalendarWeek()
    {
        var state = LiveState();
        Assert.Equal("live", state.Current.Status);
        var data = Assert.IsType<ClaudeUsage.Models.AgentSpendData>(state.Current.Data);
        Assert.False(data.IsStale);
        Assert.Equal(1.5, data.Rolling24h.TotalCost);
        Assert.Equal(6, data.CalendarWeek!.TotalCost);
        Assert.Equal(12, data.CalendarWeek.PricedRuns);
        Assert.Equal(DateTimeOffset.Parse("2026-10-05T00:00:00Z"), data.CalendarWeek.StartsAt);
        Assert.DoesNotContain(AccountA, JsonSerializer.Serialize(state.Current));
    }

    [Fact]
    public void MissingFileBeforeFirstRead_IsNotConfigured()
    {
        var state = new OpenRouterSpendState();
        state.Apply(OpenRouterSpendReader.Read(readAllText: _ => throw new FileNotFoundException()), Now);
        Assert.Equal("notConfigured", state.Current.Status);
        Assert.Null(state.Current.Data);
    }

    [Fact]
    public void InitialUnreadableFile_IsUnavailable_AndErrorDoesNotLeakContents()
    {
        var result = OpenRouterSpendReader.Read(readAllText: _ => throw new IOException("SECRET-value"));
        Assert.Equal(OpenRouterSpendReader.ReadStatus.Unavailable, result.Status);
        Assert.DoesNotContain("SECRET", result.Detail);
        var state = new OpenRouterSpendState();
        state.Apply(result, Now);
        Assert.Equal("unavailable", state.Current.Status);
        Assert.Null(state.Current.Data);
    }

    [Fact]
    public void FailureOrMissingFileAfterSuccess_KeepsLastGoodValuesStale()
    {
        var state = LiveState();
        state.Apply(OpenRouterSpendReader.Parse("{malformed"), Now);
        Assert.Equal("stale", state.Current.Status);
        Assert.True(state.Current.Data!.IsStale);
        Assert.Equal(1.5, state.Current.Data.Rolling24h.TotalCost);
        state.Apply(new(OpenRouterSpendReader.ReadStatus.Missing), Now);
        Assert.Equal("stale", state.Current.Status);
        Assert.Equal(1.5, state.Current.Data!.Rolling24h.TotalCost);
    }

    [Fact]
    public void MinimalErrorMarker_DoesNotInventZeroBalance()
    {
        var state = new OpenRouterSpendState();
        state.Apply(OpenRouterSpendReader.Parse(ErrorMarker(AccountA)), Now);
        Assert.Equal("unavailable", state.Current.Status);
        Assert.Null(state.Current.Data);
        state.Apply(new(OpenRouterSpendReader.ReadStatus.Missing), Now);
        Assert.Equal("unavailable", state.Current.Status);
    }

    [Fact]
    public void RestartCanRecoverCompleteCachedErrorSnapshot_ButOnlyAsStale()
    {
        var body = Body();
        body["source"]!["status"] = "error";
        var state = new OpenRouterSpendState();
        state.Apply(Parse(body), Now);
        Assert.Equal("stale", state.Current.Status);
        Assert.True(state.Current.Data!.IsStale);
        Assert.Equal(1.5, state.Current.Data.Rolling24h.TotalCost);
    }

    [Fact]
    public void ReconfiguredAccountWithFailedSync_ClearsPreviousAccountData()
    {
        var state = LiveState();
        state.Apply(OpenRouterSpendReader.Parse(ErrorMarker(AccountB)), Now);
        Assert.Equal("unavailable", state.Current.Status);
        Assert.Null(state.Current.Data);
    }

    [Fact]
    public void MinimalErrorWithUnknownAccount_ClearsOldData_AndIsUnavailableAtStartup()
    {
        var result = OpenRouterSpendReader.Parse("""
            {"schema_version":1,"scope":"openrouter_account","source":{"status":"error"}}
            """);
        Assert.True(result.ClearCachedData);
        var state = LiveState();
        state.Apply(result, Now);
        Assert.Equal("unavailable", state.Current.Status);
        Assert.Null(state.Current.Data);
        state = new OpenRouterSpendState();
        state.Apply(result, Now);
        Assert.Equal("unavailable", state.Current.Status);
        Assert.Null(state.Current.Data);
    }

    [Fact]
    public void ReconfiguredAccountWithMalformedTotals_AlsoClearsPreviousAccountData()
    {
        var state = LiveState();
        var body = Body();
        body["account_scope_id"] = AccountB;
        body["rolling_24h"]!["total_cost"] = null;
        state.Apply(Parse(body), Now);
        Assert.Equal("unavailable", state.Current.Status);
        Assert.Null(state.Current.Data);
    }

    [Theory]
    [InlineData("generated_at")]
    [InlineData("last_success_at")]
    public void FreshFileReadCannotRefreshOldUpstreamTimestamp(string timestamp)
    {
        var body = Body();
        var parent = timestamp == "generated_at" ? body : body["source"]!.AsObject();
        parent[timestamp] = Now.AddMinutes(-31).ToString("O");
        var state = new OpenRouterSpendState();
        // Reader deliberately has no filesystem mtime input; a new copy is not an upstream poll.
        state.Apply(OpenRouterSpendReader.Read(readAllText: _ => body.ToJsonString()), Now);
        Assert.Equal("stale", state.Current.Status);
        Assert.Equal(1.5, state.Current.Data!.Rolling24h.TotalCost);
    }

    [Theory]
    [InlineData("generated_at")]
    [InlineData("last_success_at")]
    public void TimestampMoreThanFiveMinutesInFuture_IsRejected(string timestamp)
    {
        var body = Body();
        var parent = timestamp == "generated_at" ? body : body["source"]!.AsObject();
        parent[timestamp] = Now.AddMinutes(6).ToString("O");
        var state = new OpenRouterSpendState();
        state.Apply(Parse(body), Now);
        Assert.Equal("unavailable", state.Current.Status);
        Assert.Null(state.Current.Data);
    }

    [Fact]
    public void OlderCachedExportDoesNotReplaceNewerValues()
    {
        var state = LiveState();
        var body = Body();
        body["generated_at"] = Now.AddHours(-1).ToString("O");
        body["source"]!["last_success_at"] = Now.AddHours(-1).ToString("O");
        body["rolling_24h"]!["total_cost"] = 0;
        state.Apply(Parse(body), Now);
        Assert.Equal("stale", state.Current.Status);
        Assert.Equal(1.5, state.Current.Data!.Rolling24h.TotalCost);
    }

    [Fact]
    public void ExplicitZeroAmounts_AreLegitimate()
    {
        var body = Body();
        foreach (var section in new[] { "rolling_24h", "rolling_7d", "month_to_date", "calendar_week" })
        {
            body[section]!["total_cost"] = 0;
            body[section]!["priced_runs"] = 0;
        }
        body["rolling_7d"]!["daily_average"] = 0;
        body["run_rates"]!["monthly_at_24h_pace"] = 0;
        body["run_rates"]!["monthly_at_7d_pace"] = 0;
        foreach (var section in new[] { "breakdown_by_model", "breakdown_by_provider", "daily_history" })
            body[section] = new JsonArray();
        var state = new OpenRouterSpendState();
        state.Apply(Parse(body), Now);
        Assert.Equal("live", state.Current.Status);
        Assert.Equal(0, state.Current.Data!.Rolling24h.TotalCost);
    }

    [Theory]
    [InlineData("launcher")]
    [InlineData("openrouter_key")]
    [InlineData("")]
    public void WrongOrMissingScope_IsNeverAccepted(string scope)
    {
        var body = Body();
        if (scope.Length == 0) body.Remove("scope");
        else body["scope"] = scope;
        Assert.Null(Parse(body).Data);
    }

    public static IEnumerable<object[]> NumericFields()
    {
        foreach (var section in new[] { "rolling_24h", "rolling_7d", "month_to_date", "calendar_week" })
            foreach (var field in new[] { "total_cost", "priced_runs", "unpriced_runs" })
                yield return new object[] { section, field };
        yield return new object[] { "rolling_7d", "daily_average" };
        yield return new object[] { "run_rates", "monthly_at_24h_pace" };
        yield return new object[] { "run_rates", "monthly_at_7d_pace" };
    }

    [Theory]
    [MemberData(nameof(NumericFields))]
    public void MissingNullNegativeAndStringNumbers_DoNotSilentlyBecomeZero(string section, string field)
    {
        foreach (var invalid in new JsonNode?[] { null, JsonValue.Create(-1), JsonValue.Create("0") })
        {
            var body = Body();
            body[section]![field] = invalid;
            Assert.Null(Parse(body).Data);
        }
        var missing = Body();
        missing[section]!.AsObject().Remove(field);
        Assert.Null(Parse(missing).Data);
    }

    [Fact]
    public void NonFiniteCosts_AndFractionalCounts_AreRejected()
    {
        Assert.Null(OpenRouterSpendReader.Parse(ValidBody.Replace("\"total_cost\": 1.5", "\"total_cost\": 1e400")).Data);
        var body = Body();
        body["rolling_24h"]!["priced_runs"] = 0.5;
        Assert.Null(Parse(body).Data);
    }

    [Theory]
    [InlineData("breakdown_by_model")]
    [InlineData("breakdown_by_provider")]
    [InlineData("daily_history")]
    public void MissingOrMalformedBreakdownArrays_AreRejected(string section)
    {
        var body = Body();
        body.Remove(section);
        Assert.Null(Parse(body).Data);
        body = Body();
        body[section] = null;
        Assert.Null(Parse(body).Data);
        body = Body();
        body[section]![0]!["cost"] = null;
        Assert.Null(Parse(body).Data);
        body = Body();
        body[section]![0]!["priced_runs"] = -1;
        Assert.Null(Parse(body).Data);
    }

    [Fact]
    public void InvalidLabelsDatesAndTokens_AreRejected()
    {
        var body = Body();
        body["breakdown_by_model"]![0]!["model"] = "bad\nlabel";
        Assert.Null(Parse(body).Data);
        body = Body();
        body["breakdown_by_model"]![0]!["input_tokens"] = -1;
        Assert.Null(Parse(body).Data);
        body = Body();
        body["calendar_week"]!["starts_at"] = null;
        Assert.Null(Parse(body).Data);
        body = Body();
        body["daily_history"]![0]!["date"] = "2026-02-31";
        Assert.Null(Parse(body).Data);
    }

    [Fact]
    public void DefaultPath_IsAccountExport_NotLauncherExport()
    {
        Assert.EndsWith(Path.Combine("ClaudeUsage", "openrouter-spend.v1.json"), OpenRouterSpendReader.FilePath);
        Assert.DoesNotContain("AgentsInference", OpenRouterSpendReader.FilePath);
    }

    [Fact]
    public void RuntimeUsesOnlyAccountSpendLane_AndSharesTheSameSnapshotContract()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        var poller = File.ReadAllText(Path.Combine(root, "Services", "OpenRouterSpendPoller.cs"));
        var html = File.ReadAllText(Path.Combine(root, "wwwroot", "index.html"));

        Assert.Contains("AddSingleton<OpenRouterSpendState>()", app);
        Assert.Contains("GetRequiredService<OpenRouterSpendPoller>()", app);
        Assert.DoesNotContain("AddSingleton<AgentSpendState>()", app);
        Assert.DoesNotContain("GetRequiredService<AgentSpendPoller>()", app);
        Assert.Contains("app.MapGet(\"/api/openrouter-spend\", (OpenRouterSpendState state) => Results.Ok(state.Current))", app);
        Assert.Contains("app.MapGet(\"/api/agent-spend\", () => Results.StatusCode(410))", app);
        Assert.Contains("SendAsync(\"openRouterSpendUpdated\", _state.Current, stoppingToken)", poller);
        Assert.Contains("IntervalSec = 180", poller);
        Assert.Contains("OpenRouterSpendReader.Read()", poller);
        Assert.DoesNotContain("AgentSpendReader", poller);
        Assert.Contains("/api/openrouter-spend", html);
        Assert.Contains("openRouterSpendUpdated", html);
    }
}
