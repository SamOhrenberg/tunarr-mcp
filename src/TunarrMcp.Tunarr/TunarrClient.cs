using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    // Tunarr's schemas accept a missing optional field but reject null, so request bodies leave nulls out.
    private static readonly JsonSerializerOptions JsonWrite = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

    /// <summary>GET /media-sources</summary>
    public async Task<IReadOnlyList<MediaSource>> GetMediaSourcesAsync(CancellationToken ct = default) =>
        await GetAsync<List<MediaSource>>("media-sources", ct).ConfigureAwait(false);

    /// <summary>
    /// POST /programs/search (read-only). Text queries match titles only and rank by relevance: Tunarr's defaults
    /// search every field, summaries included, and sort by title, so "bluey" would return Black Mirror on page one.
    /// </summary>
    public Task<ProgramSearchResult> SearchProgramsAsync(LibrarySearch search, CancellationToken ct = default)
    {
        var hasQuery = !string.IsNullOrWhiteSpace(search.Query);

        var filters = new List<SearchFilter>();
        if (search.Types.Count > 0)
            filters.Add(Facet("type", search.Types));
        if (!string.IsNullOrWhiteSpace(search.Genre))
            filters.Add(Facet("genres.name", [search.Genre]));

        var request = new ProgramSearchRequest(
            new ProgramSearchQuery(
                hasQuery ? search.Query : null,
                hasQuery ? ["title"] : null,
                filters.Count switch { 0 => null, 1 => filters[0], _ => new SearchFilterOperator("and", filters) },
                hasQuery ? [] : null), // empty: relevance order; null: Tunarr's title order, fine for browsing
            search.MediaSourceId,
            // Tunarr pages text queries from 1 and browsing from 0, and a text query always returns 20 per page.
            Page: hasQuery ? search.Page : search.Page - 1,
            Limit: SearchPageSize,
            ExpandParents: true);

        return PostAsync<ProgramSearchRequest, ProgramSearchResult>("programs/search", request, ct);

        static SearchFilterValue Facet(string key, IReadOnlyList<string> values) =>
            new(new SearchFieldSpec(key, "faceted_string", "in", values));
    }

    /// <summary>Results per search page. Tunarr ignores the requested limit for text queries and returns 20.</summary>
    public const int SearchPageSize = 20;

    /// <summary>GET /program_groupings/:id. A show with its seasons; null when there's no grouping with that id.</summary>
    public Task<Show?> GetShowAsync(string showId, CancellationToken ct = default) =>
        GetOrNotFoundAsync<Show>($"program_groupings/{Uri.EscapeDataString(showId)}", ct);

    /// <summary>GET /programs/:id/children for a season: its episodes.</summary>
    public Task<SeasonChildren> GetSeasonEpisodesAsync(string seasonId, CancellationToken ct = default) =>
        GetAsync<SeasonChildren>($"programs/{Uri.EscapeDataString(seasonId)}/children", ct);

    private async Task<T?> GetOrNotFoundAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            return await GetOptionalAsync<T>(path, ct).ConfigureAwait(false);
        }
        catch (TunarrApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken ct)
    {
        using var content = JsonContent.Create(body, options: JsonWrite);
        using var response = await http.PostAsync(new Uri(path, UriKind.Relative), content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, HttpMethod.Post, path, ct).ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return (bytes.Length == 0 ? default : JsonSerializer.Deserialize<TResponse>(bytes, Json))
               ?? throw new TunarrApiException(HttpMethod.Post, path, response.StatusCode, "Tunarr returned an empty body.");
    }

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
