using System.Net;
using System.Text.Json;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Tunarr;

/// <summary>
/// Thin facade over Tunarr's REST API. The HttpClient's BaseAddress must point at the API root
/// (for example http://tunarr:8000/api/) and end with a slash; paths here are relative to it.
/// Methods are added as tools need them, with DTOs that cover only the fields the tools use.
/// </summary>
public sealed class TunarrClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>GET /version</summary>
    public Task<TunarrVersion> GetVersionAsync(CancellationToken ct = default) =>
        GetAsync<TunarrVersion>("version", ct);

    /// <summary>GET /system/health. One entry per Tunarr health check, keyed by check name.</summary>
    public async Task<IReadOnlyDictionary<string, HealthCheckResult>> GetHealthAsync(CancellationToken ct = default) =>
        await GetAsync<Dictionary<string, HealthCheckResult>>("system/health", ct).ConfigureAwait(false);

    /// <summary>GET /tasks</summary>
    public async Task<IReadOnlyList<TunarrTask>> GetTasksAsync(CancellationToken ct = default) =>
        await GetAsync<List<TunarrTask>>("tasks", ct).ConfigureAwait(false);

    /// <summary>GET /guide/status</summary>
    public Task<GuideStatus> GetGuideStatusAsync(CancellationToken ct = default) =>
        GetAsync<GuideStatus>("guide/status", ct);

    /// <summary>GET /channels</summary>
    public async Task<IReadOnlyList<Channel>> GetChannelsAsync(CancellationToken ct = default) =>
        await GetAsync<List<Channel>>("channels", ct).ConfigureAwait(false);

    /// <summary>GET /channels/:id/schedule. Null when the channel has no slot schedule.</summary>
    public Task<ChannelScheduleResponse?> GetChannelScheduleAsync(string channelId, CancellationToken ct = default) =>
        GetOptionalAsync<ChannelScheduleResponse>($"channels/{Uri.EscapeDataString(channelId)}/schedule", ct);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        await GetOptionalAsync<T>(path, ct).ConfigureAwait(false)
        ?? throw new TunarrApiException(HttpMethod.Get, path, HttpStatusCode.OK, "Tunarr returned an empty body.");

    /// <summary>Some routes answer "nothing here" with 200 and no body rather than 404; that maps to null.</summary>
    private async Task<T?> GetOptionalAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, HttpMethod.Get, path, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return body.Length == 0 ? default : JsonSerializer.Deserialize<T>(body, Json);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, HttpMethod method, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new TunarrApiException(method, path, response.StatusCode, TunarrApiException.Truncate(body));
    }
}
