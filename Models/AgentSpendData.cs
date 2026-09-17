namespace ClaudeUsage.Models;

// Everything here mirrors the Agents & Inference "spend-summary.v1" contract file field-for-field.
// ClaudeUsage never recomputes rolling-window totals, pricing, run-rates, or model/provider
// aggregation -- Agents & Inference owns all of that; this is a pure pass-through model of what
// they already published. Cost fields are full-precision USD doubles (thousandths of a dollar are
// normal), never formatted strings.

// source.status is a free-form string from the producer ("ok" is the only value ClaudeUsage acts
// on -- anything else means "don't trust this read", see AgentSpendFreshnessPolicy).
public record AgentSpendSource(string Status, DateTimeOffset? LastSuccessAt);

public record AgentSpendRolling24h(double TotalCost, int PricedRuns, int UnpricedRuns);

public record AgentSpendRolling7d(double TotalCost, double DailyAverage, int PricedRuns, int UnpricedRuns);

public record AgentSpendMonthToDate(double TotalCost, int PricedRuns, int UnpricedRuns);

public record AgentSpendRunRates(double MonthlyAt24hPace, double MonthlyAt7dPace);

// Per-model breakdown row. The contract names the breakdown_by_model[] array but not its per-item
// shape, so these fields are ClaudeUsage's best-effort read of a representative row (model,
// provider, cost, run counts, token totals) -- every field is read tolerantly (see
// AgentSpendParser) so an item missing any of these simply reads as 0/null rather than dropping
// the whole row.
public record AgentSpendModelBreakdown(
    string Model,
    string? Provider,
    double Cost,
    int PricedRuns,
    int UnpricedRuns,
    long InputTokens,
    long OutputTokens);

public record AgentSpendProviderBreakdown(string Provider, double Cost, int PricedRuns, int UnpricedRuns);

// One local-calendar-day point (daily_history[], last 31 days per the contract).
public record AgentSpendDailyPoint(string Date, double Cost, int PricedRuns, int UnpricedRuns);

public record AgentSpendData(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string? DataThrough,
    string Currency,
    string? Timezone,
    AgentSpendSource Source,
    AgentSpendRolling24h Rolling24h,
    AgentSpendRolling7d Rolling7d,
    AgentSpendMonthToDate MonthToDate,
    AgentSpendRunRates RunRates,
    IReadOnlyList<AgentSpendModelBreakdown> BreakdownByModel,
    IReadOnlyList<AgentSpendProviderBreakdown> BreakdownByProvider,
    IReadOnlyList<AgentSpendDailyPoint> DailyHistory,
    // Set by AgentSpendPoller -- true whenever the CURRENT snapshot is not a fresh, status=="ok"
    // read (a missing/malformed file, a non-"ok" source status, or a local summary file that has
    // not been refreshed within AgentSpendFreshnessPolicy.FreshWindow). Never implies zeroed
    // figures -- it always carries the last good snapshot, just flagged.
    bool IsStale = false);
