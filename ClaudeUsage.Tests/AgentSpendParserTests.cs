using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

// Covers AgentSpendParser: the tolerant, pure parse of the Agents & Inference spend-summary
// contract file. Nothing here recomputes rolling-window/pricing/run-rate/model-provider
// aggregation -- every asserted value is read straight off the JSON, not derived.
public class AgentSpendParserTests
{
    private const string FullBody = """
    {
      "schema_version": "v1",
      "generated_at": "2026-09-17T14:32:00Z",
      "data_through": "2026-09-17",
      "currency": "USD",
      "timezone": "Europe/London",
      "source": { "status": "ok", "last_success_at": "2026-09-17T14:30:00Z" },
      "rolling_24h": { "total_cost": 4.125, "priced_runs": 12, "unpriced_runs": 2 },
      "rolling_7d": { "total_cost": 28.75, "daily_average": 4.107142857142857, "priced_runs": 80, "unpriced_runs": 5 },
      "month_to_date": { "total_cost": 61.005, "priced_runs": 150, "unpriced_runs": 9 },
      "run_rates": { "monthly_at_24h_pace": 123.75, "monthly_at_7d_pace": 128.36 },
      "breakdown_by_model": [
        { "model": "claude-sonnet-5", "provider": "anthropic", "cost": 40.5, "priced_runs": 100, "unpriced_runs": 3, "input_tokens": 500000, "output_tokens": 120000 },
        { "model": "gpt-5", "provider": "openai", "cost": 20.505, "priced_runs": 50, "unpriced_runs": 6, "input_tokens": 200000, "output_tokens": 40000 }
      ],
      "breakdown_by_provider": [
        { "provider": "anthropic", "cost": 40.5, "priced_runs": 100, "unpriced_runs": 3 },
        { "provider": "openai", "cost": 20.505, "priced_runs": 50, "unpriced_runs": 6 }
      ],
      "daily_history": [
        { "date": "2026-09-16", "cost": 3.2, "priced_runs": 10, "unpriced_runs": 1 },
        { "date": "2026-09-17", "cost": 4.125, "priced_runs": 12, "unpriced_runs": 2 }
      ]
    }
    """;

    [Fact]
    public void FullBody_ParsesEveryContractField()
    {
        var d = AgentSpendParser.Parse(FullBody);

        Assert.NotNull(d);
        Assert.Equal("v1", d!.SchemaVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-09-17T14:32:00Z"), d.GeneratedAt);
        Assert.Equal("2026-09-17", d.DataThrough);
        Assert.Equal("USD", d.Currency);
        Assert.Equal("Europe/London", d.Timezone);

        Assert.Equal("ok", d.Source.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-09-17T14:30:00Z"), d.Source.LastSuccessAt);

        Assert.Equal(4.125, d.Rolling24h.TotalCost);
        Assert.Equal(12, d.Rolling24h.PricedRuns);
        Assert.Equal(2, d.Rolling24h.UnpricedRuns);

        Assert.Equal(28.75, d.Rolling7d.TotalCost);
        Assert.Equal(80, d.Rolling7d.PricedRuns);
        Assert.Equal(5, d.Rolling7d.UnpricedRuns);

        Assert.Equal(61.005, d.MonthToDate.TotalCost);
        Assert.Equal(123.75, d.RunRates.MonthlyAt24hPace);
        Assert.Equal(128.36, d.RunRates.MonthlyAt7dPace);

        Assert.Equal(2, d.BreakdownByModel.Count);
        Assert.Equal("claude-sonnet-5", d.BreakdownByModel[0].Model);
        Assert.Equal("anthropic", d.BreakdownByModel[0].Provider);
        Assert.Equal(500000, d.BreakdownByModel[0].InputTokens);
        Assert.Equal(120000, d.BreakdownByModel[0].OutputTokens);

        Assert.Equal(2, d.BreakdownByProvider.Count);
        Assert.Equal("openai", d.BreakdownByProvider[1].Provider);

        Assert.Equal(2, d.DailyHistory.Count);
        Assert.Equal("2026-09-17", d.DailyHistory[1].Date);
        Assert.False(d.IsStale);   // parser never sets this -- only AgentSpendPoller does
    }

    [Fact]
    public void SubCentPrecision_PreservedExactly()
    {
        const string body = """
        {
          "schema_version": "v1",
          "generated_at": "2026-09-17T00:00:00Z",
          "currency": "USD",
          "source": { "status": "ok" },
          "rolling_24h": { "total_cost": 0.0015, "priced_runs": 1, "unpriced_runs": 0 },
          "rolling_7d": { "total_cost": 0.0015, "daily_average": 0.000214286, "priced_runs": 1, "unpriced_runs": 0 },
          "month_to_date": { "total_cost": 0.0015, "priced_runs": 1, "unpriced_runs": 0 },
          "run_rates": { "monthly_at_24h_pace": 0.045, "monthly_at_7d_pace": 0.00643 }
        }
        """;

        var d = AgentSpendParser.Parse(body);

        Assert.NotNull(d);
        Assert.Equal(0.0015, d!.Rolling24h.TotalCost);
        Assert.Equal(0.0015, d.MonthToDate.TotalCost);
        Assert.Equal(0.045, d.RunRates.MonthlyAt24hPace);
    }

    [Fact]
    public void ZeroSpend_ParsesAsExactZero_NotDropped()
    {
        const string body = """
        {
          "schema_version": "v1",
          "generated_at": "2026-09-17T00:00:00Z",
          "currency": "USD",
          "source": { "status": "ok" },
          "rolling_24h": { "total_cost": 0, "priced_runs": 0, "unpriced_runs": 0 },
          "rolling_7d": { "total_cost": 0, "daily_average": 0, "priced_runs": 0, "unpriced_runs": 0 },
          "month_to_date": { "total_cost": 0, "priced_runs": 0, "unpriced_runs": 0 },
          "run_rates": { "monthly_at_24h_pace": 0, "monthly_at_7d_pace": 0 }
        }
        """;

        var d = AgentSpendParser.Parse(body);

        Assert.NotNull(d);
        Assert.Equal(0, d!.Rolling24h.TotalCost);
        Assert.Equal(0, d.MonthToDate.PricedRuns);
        Assert.Empty(d.BreakdownByModel);
        Assert.Empty(d.DailyHistory);
    }

    [Fact]
    public void UnpricedRunsOnly_SurfacedCorrectly()
    {
        const string body = """
        {
          "schema_version": "v1",
          "generated_at": "2026-09-17T00:00:00Z",
          "currency": "USD",
          "source": { "status": "ok" },
          "rolling_24h": { "total_cost": 0, "priced_runs": 0, "unpriced_runs": 4 },
          "rolling_7d": { "total_cost": 0, "daily_average": 0, "priced_runs": 0, "unpriced_runs": 10 },
          "month_to_date": { "total_cost": 0, "priced_runs": 0, "unpriced_runs": 15 },
          "run_rates": { "monthly_at_24h_pace": 0, "monthly_at_7d_pace": 0 }
        }
        """;

        var d = AgentSpendParser.Parse(body);

        Assert.NotNull(d);
        Assert.Equal(0, d!.MonthToDate.PricedRuns);
        Assert.Equal(15, d.MonthToDate.UnpricedRuns);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{ "schema_version": "v1" }""")]   // missing generated_at/source/windows
    [InlineData("""{ "schema_version": "v1", "generated_at": "2026-09-17T00:00:00Z", "source": { "status": "ok" } }""")]   // missing windows
    [InlineData("""{ "schema_version": "v1", "generated_at": "not-a-date", "source": { "status": "ok" }, "rolling_24h": {}, "rolling_7d": {}, "month_to_date": {}, "run_rates": {} }""")]
    public void MalformedOrIncomplete_YieldsNull_NoThrow(string body)
    {
        var d = AgentSpendParser.Parse(body);

        Assert.Null(d);
    }

    [Fact]
    public void MissingSourceStatus_YieldsNull()
    {
        const string body = """
        {
          "schema_version": "v1",
          "generated_at": "2026-09-17T00:00:00Z",
          "currency": "USD",
          "source": { "last_success_at": "2026-09-17T00:00:00Z" },
          "rolling_24h": { "total_cost": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "rolling_7d": { "total_cost": 1, "daily_average": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "month_to_date": { "total_cost": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "run_rates": { "monthly_at_24h_pace": 1, "monthly_at_7d_pace": 1 }
        }
        """;

        Assert.Null(AgentSpendParser.Parse(body));
    }

    [Fact]
    public void MissingCurrency_DefaultsToUsd()
    {
        const string body = """
        {
          "schema_version": "v1",
          "generated_at": "2026-09-17T00:00:00Z",
          "source": { "status": "ok" },
          "rolling_24h": { "total_cost": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "rolling_7d": { "total_cost": 1, "daily_average": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "month_to_date": { "total_cost": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "run_rates": { "monthly_at_24h_pace": 1, "monthly_at_7d_pace": 1 }
        }
        """;

        var d = AgentSpendParser.Parse(body);

        Assert.NotNull(d);
        Assert.Equal("USD", d!.Currency);
    }

    [Fact]
    public void ModelBreakdownItem_MissingOptionalFields_DefaultsToZeroOrNull_RowStillIncluded()
    {
        const string body = """
        {
          "schema_version": "v1",
          "generated_at": "2026-09-17T00:00:00Z",
          "source": { "status": "ok" },
          "rolling_24h": { "total_cost": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "rolling_7d": { "total_cost": 1, "daily_average": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "month_to_date": { "total_cost": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "run_rates": { "monthly_at_24h_pace": 1, "monthly_at_7d_pace": 1 },
          "breakdown_by_model": [ { "model": "some-model", "cost": 1.5 } ]
        }
        """;

        var d = AgentSpendParser.Parse(body);

        Assert.NotNull(d);
        var m = Assert.Single(d!.BreakdownByModel);
        Assert.Equal("some-model", m.Model);
        Assert.Null(m.Provider);
        Assert.Equal(0, m.PricedRuns);
        Assert.Equal(0, m.InputTokens);
    }

    [Fact]
    public void ModelBreakdownItem_MissingModelName_IsDropped()
    {
        const string body = """
        {
          "schema_version": "v1",
          "generated_at": "2026-09-17T00:00:00Z",
          "source": { "status": "ok" },
          "rolling_24h": { "total_cost": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "rolling_7d": { "total_cost": 1, "daily_average": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "month_to_date": { "total_cost": 1, "priced_runs": 1, "unpriced_runs": 0 },
          "run_rates": { "monthly_at_24h_pace": 1, "monthly_at_7d_pace": 1 },
          "breakdown_by_model": [ { "cost": 1.5 }, { "model": "kept", "cost": 2 } ]
        }
        """;

        var d = AgentSpendParser.Parse(body);

        Assert.NotNull(d);
        var m = Assert.Single(d!.BreakdownByModel);
        Assert.Equal("kept", m.Model);
    }
}
