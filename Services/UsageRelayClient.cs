using System.Net;
using System.Text.Json;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

// Independent peers are checked concurrently with a bounded timeout. Diagnostics omit URLs and tokens.
public sealed class UsageRelayClient(HttpClient http, Func<UsageRelaySource[]?> sources)
{
    public bool HasSources => sources()?.Length > 0;
    public string LastFailure { get; private set; } = "";

    public async Task<UsageData?> TryFetchFreshAsync(CancellationToken ct)
    {
        var configured = sources() ?? [];
        LastFailure = "";
        var results = await Task.WhenAll(configured.Take(8).Select(s => FetchAsync(s, ct)));
        LastFailure = string.Join("; ", results.Where(r => r.Data is null).Select(r => r.Status));
        return results.Where(r => r.Data is not null).Select(r => r.Data!)
            .OrderByDescending(d => d.FetchedAt).FirstOrDefault();
    }

    private async Task<(UsageData? Data, string Status)> FetchAsync(UsageRelaySource source, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(source.Name) ? "Remote computer" : source.Name[..Math.Min(source.Name.Length, 80)];
        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            string.IsNullOrWhiteSpace(source.AccessToken))
            return (null, name + ": check connection settings");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("X-Claude-Usage-Relay-Token", source.AccessToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (null, name + ": relay access key rejected");
            if (!response.IsSuccessStatusCode)
                return (null, name + ": relay reachable, but Claude usage is temporarily unavailable");
            await response.Content.LoadIntoBufferAsync(256 * 1024, timeout.Token);
            var data = JsonSerializer.Deserialize<UsageData>(await response.Content.ReadAsStringAsync(timeout.Token),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (!UsageRelayPolicy.IsFresh(data, DateTimeOffset.UtcNow))
                return (null, name + ": waiting for a fresh usage reading");
            return (data! with { Source = name }, name + ": connected");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (null, name + ": connection timed out"); }
        catch (HttpRequestException) { return (null, name + ": relay unreachable"); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException or InvalidOperationException)
        { return (null, name + ": invalid relay response or settings"); }
    }
}
