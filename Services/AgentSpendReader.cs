using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Read-only reader for the Agents & Inference spend-summary contract file. An INDEPENDENT failure
// domain from Claude/Codex: reading, parsing, or a missing/malformed file here can never disturb
// either usage lane, and vice versa. NEVER reads the raw ledger, never calls any inference
// provider -- Agents & Inference owns all aggregation; this only reads their published summary
// file, which is exactly what the READ BOUNDARY for this feature requires.
public static class AgentSpendReader
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DabbleLabs", "AgentsInference", "spend-summary.v1.json");

    public enum ReadStatus { Ok, Missing, Malformed }

    public sealed record ReadResult(ReadStatus Status, AgentSpendData? Data);

    // path override exists purely so tests can point this at a temp file instead of the real
    // %LOCALAPPDATA% location; production callers always use the default (FilePath).
    public static ReadResult Read(string? path = null)
    {
        path ??= FilePath;
        string json;
        try
        {
            if (!File.Exists(path)) return new ReadResult(ReadStatus.Missing, null);
            json = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return new ReadResult(ReadStatus.Missing, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new ReadResult(ReadStatus.Missing, null);
        }

        var data = AgentSpendParser.Parse(json);
        return data is null
            ? new ReadResult(ReadStatus.Malformed, null)
            : new ReadResult(ReadStatus.Ok, data);
    }
}
