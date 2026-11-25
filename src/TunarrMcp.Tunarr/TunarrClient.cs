using System.Net.Http.Json;
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

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, HttpMethod.Get, path, ct).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
               ?? throw new TunarrApiException(HttpMethod.Get, path, response.StatusCode, "Tunarr returned an empty body.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, HttpMethod method, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new TunarrApiException(method, path, response.StatusCode, TunarrApiException.Truncate(body));
    }
}
