using Microsoft.Extensions.Hosting;

namespace ClaudeUsage.Services;

public sealed class RelaySnapshotPoller(UsageService usage, RelaySnapshotStore store,
    SettingsService settings, PollLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One request at a time, regardless of how many viewers connect. Auth/rate-limit cooldowns
        // remain owned by UsageService; HTTP reads never shorten this three-minute cadence.
        while (!stoppingToken.IsCancellationRequested)
        {
            settings.Reload();
            try { store.Success(await usage.FetchAsync()); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                store.Failure();
                log.LogFailure("Relay background poll failed: " + ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
        }
    }
}
