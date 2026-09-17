using ClaudeUsage.Services;
using Xunit;

namespace ClaudeUsage.Tests;

// Covers AgentSpendReader's file-level classification (Ok / Missing / Malformed), which is what
// AgentSpendPoller relies on to decide whether to adopt a read or retain the last-good snapshot.
// Uses a temp-file path override (Read(path)) so these never touch the real
// %LOCALAPPDATA%\DabbleLabs\AgentsInference\spend-summary.v1.json production location.
public class AgentSpendReaderTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"spend-summary-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch { /* best-effort cleanup */ }
    }

    private const string ValidBody = """
    {
      "schema_version": "v1",
      "generated_at": "2026-09-17T00:00:00Z",
      "currency": "USD",
      "source": { "status": "ok" },
      "rolling_24h": { "total_cost": 1, "priced_runs": 1, "unpriced_runs": 0 },
      "rolling_7d": { "total_cost": 1, "daily_average": 1, "priced_runs": 1, "unpriced_runs": 0 },
      "month_to_date": { "total_cost": 1, "priced_runs": 1, "unpriced_runs": 0 },
      "run_rates": { "monthly_at_24h_pace": 1, "monthly_at_7d_pace": 1 }
    }
    """;

    [Fact]
    public void MissingFile_ReturnsMissing_NoThrow()
    {
        var result = AgentSpendReader.Read(_tempPath);   // never written

        Assert.Equal(AgentSpendReader.ReadStatus.Missing, result.Status);
        Assert.Null(result.Data);
        Assert.False(result.FileExisted);
        Assert.Contains("File.Exists()", result.Detail);
    }

    [Fact]
    public void MalformedFile_ReturnsMalformed_NoThrow()
    {
        File.WriteAllText(_tempPath, "{ this is not valid json");

        var result = AgentSpendReader.Read(_tempPath);

        Assert.Equal(AgentSpendReader.ReadStatus.Malformed, result.Status);
        Assert.Null(result.Data);
        Assert.True(result.FileExisted);   // the file WAS found -- only its contents were bad
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void IncompleteJson_MissingRequiredFields_ReturnsMalformed()
    {
        File.WriteAllText(_tempPath, """{ "schema_version": "v1" }""");

        var result = AgentSpendReader.Read(_tempPath);

        Assert.Equal(AgentSpendReader.ReadStatus.Malformed, result.Status);
    }

    [Fact]
    public void ValidFile_ReturnsOkWithData()
    {
        File.WriteAllText(_tempPath, ValidBody);

        var result = AgentSpendReader.Read(_tempPath);

        Assert.Equal(AgentSpendReader.ReadStatus.Ok, result.Status);
        Assert.NotNull(result.Data);
        Assert.Equal("ok", result.Data!.Source.Status);
        Assert.True(result.FileExisted);
    }

    [Fact]
    public void SourceStatusNotOk_StillReadsOk_PolicyDecidesAdoption()
    {
        // The reader only classifies parse success/failure -- "should we trust source.status" is
        // AgentSpendFreshnessPolicy's job (see AgentSpendFreshnessPolicyTests), not the reader's.
        var body = ValidBody.Replace("\"status\": \"ok\"", "\"status\": \"degraded\"");
        File.WriteAllText(_tempPath, body);

        var result = AgentSpendReader.Read(_tempPath);

        Assert.Equal(AgentSpendReader.ReadStatus.Ok, result.Status);
        Assert.Equal("degraded", result.Data!.Source.Status);
    }

    [Fact]
    public void DefaultPath_PointsAtLocalAppDataDabbleLabsAgentsInference()
    {
        Assert.Contains(Path.Combine("DabbleLabs", "AgentsInference", "spend-summary.v1.json"), AgentSpendReader.FilePath);
    }
}
