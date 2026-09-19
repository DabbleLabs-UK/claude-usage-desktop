using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

public static class UsageSourceSelector
{
    public static async Task<UsageData> FetchAsync(UsageRelayClient relay,
        Func<Task<UsageData>> local, CancellationToken ct)
    {
        var remote = await relay.TryFetchFreshAsync(ct);
        if (remote is not null) return remote;
        try
        {
            return (await local()) with
            { Source = relay.HasSources ? "This PC (remote source unavailable)" : "This PC" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) when (relay.HasSources)
        {
            throw new UsageSourcesUnavailableException(relay.LastFailure +
                "; this PC could not supply a fresh reading either. Retrying automatically.");
        }
    }
}
