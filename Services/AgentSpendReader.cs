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

    // FileUpdatedAt is the local sync heartbeat. The remote summary is regenerated only when the
    // launcher records a run, but a successful Windows sync atomically rewrites this file every
    // five minutes. Its timestamp therefore distinguishes an idle launcher from a broken sync.
    public sealed record ReadResult(
        ReadStatus Status,
        AgentSpendData? Data,
        bool FileExisted,
        string? Detail = null,
        DateTimeOffset? FileUpdatedAt = null);

    // path override exists purely so tests can point this at a temp file instead of the real
    // %LOCALAPPDATA% location; production callers always use the default (FilePath).
    public static ReadResult Read(string? path = null)
    {
        path ??= FilePath;

        bool exists;
        try
        {
            exists = File.Exists(path);
        }
        catch (Exception ex)
        {
            // Even the existence check can throw (e.g. an inaccessible/unmapped path) -- capture it
            // rather than letting it propagate, same as every other fault in this reader.
            return new ReadResult(ReadStatus.Missing, null, false, $"File.Exists threw {ex.GetType().Name}: {ex.Message}");
        }

        if (!exists)
            return new ReadResult(ReadStatus.Missing, null, false, "File.Exists() returned false");

        string json;
        DateTimeOffset fileUpdatedAt;
        try
        {
            json = File.ReadAllText(path);
            fileUpdatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }
        catch (IOException ex)
        {
            return new ReadResult(ReadStatus.Missing, null, true, $"IOException reading file: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ReadResult(ReadStatus.Missing, null, true, $"UnauthorizedAccessException reading file: {ex.Message}");
        }

        var data = AgentSpendParser.Parse(json, out var parseFailureReason);
        return data is null
            ? new ReadResult(ReadStatus.Malformed, null, true,
                $"AgentSpendParser.Parse returned null for a {json.Length}-char body: {parseFailureReason ?? "(no reason captured)"}",
                fileUpdatedAt)
            : new ReadResult(ReadStatus.Ok, data, true, FileUpdatedAt: fileUpdatedAt);
    }
}
