using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeUsage.Models;
using Microsoft.Extensions.Logging;

namespace ClaudeUsage.Services;

// Reads a small, data-only snapshot from optional peer relays. Relays are deliberately opt-in:
// no default URL, discovery, SSH, host name, or credential copying exists here. A healthy relay
// wins over this PC's local login because it represents the machine where Claude is being used.
public sealed class UsageRelayClient
{
    private static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromMinutes(10);
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly ILogger<UsageRelayClient> _logger;

    public UsageRelayClient(HttpClient http, SettingsService settings, ILogger<UsageRelayClient> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public async Task<UsageData?> TryFetchFreshAsync(CancellationToken ct)
    {
        var sources = _settings.Current.UsageRelaySources;
        if (sources is null || sources.Length == 0) return null;

        UsageData? newest = null;
        string? newestName = null;
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Url) ||
                string.IsNullOrWhiteSpace(source.AccessToken) ||
                !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                _logger.LogWarning("Ignoring an invalid Claude Usage relay configuration.");
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Add("X-Claude-Usage-Relay-Token", source.AccessToken);
                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Usage relay {Relay} returned HTTP {Status}.", source.Name, (int)response.StatusCode);
                    continue;
                }

                var data = await response.Content.ReadFromJsonAsync<UsageData>(cancellationToken: ct);
                if (data is null || data.Windows.Count == 0 || DateTimeOffset.UtcNow - data.FetchedAt > MaxSnapshotAge)
                {
                    _logger.LogWarning("Usage relay {Relay} returned no fresh snapshot.", source.Name);
                    continue;
                }

                if (newest is null || data.FetchedAt > newest.FetchedAt)
                {
                    newest = data with { IsStale = false };
                    newestName = source.Name;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogWarning(ex, "Usage relay {Relay} is unavailable.", source.Name);
            }
        }

        if (newest is not null)
            _logger.LogInformation("Using fresh Claude usage snapshot from relay {Relay} ({FetchedAt}).", newestName, newest.FetchedAt);
        return newest;
    }
}
