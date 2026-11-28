using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>GET /channels/:id/programming. A page of the lineup; null when there's no such channel.</summary>
    public Task<ChannelLineup?> GetChannelLineupAsync(string channelId, int offset, int limit, CancellationToken ct = default) =>
        GetOrNotFoundAsync<ChannelLineup>(
            $"channels/{Uri.EscapeDataString(channelId)}/programming?offset={offset}&limit={limit}", ct);

    /// <summary>
    /// GET /channels/:id/programming, whole: just the lineup items, as Tunarr's JSON. They're the same shape
    /// <see cref="ReplaceLineupAsync"/> sends back, so this is what a snapshot stores.
    /// </summary>
    public async Task<string> GetLineupItemsJsonAsync(string channelId, CancellationToken ct = default)
    {
        var path = $"channels/{Uri.EscapeDataString(channelId)}/programming";
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, HttpMethod.Get, path, ct).ConfigureAwait(false);

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        return json.RootElement.TryGetProperty("lineup", out var lineup) && lineup.ValueKind == JsonValueKind.Array
            ? lineup.GetRawText()
            : throw new TunarrApiException(HttpMethod.Get, path, response.StatusCode, "The response has no lineup.");
    }

    /// <summary>
    /// POST /channels/:id/programming, manual, append: adds programs at the end of the lineup, like "Add media" in
    /// Tunarr's UI. The programs need their real durations (from <see cref="GetProgramAsync"/>).
    /// </summary>
    public Task<SavedLineup> AppendToLineupAsync(string channelId, IReadOnlyList<ProgramDetails> programs, CancellationToken ct = default)
    {
        var lineup = new JsonArray(programs
            .Select(p => (JsonNode)new JsonObject { ["type"] = "content", ["id"] = p.Uuid, ["duration"] = p.Duration })
            .ToArray());
        return SaveManualLineupAsync(channelId, lineup, append: true, ct);
    }

    /// <summary>POST /channels/:id/programming, manual: replaces the lineup with items from <see cref="GetLineupItemsJsonAsync"/>.</summary>
    public Task<SavedLineup> ReplaceLineupAsync(string channelId, string lineupItemsJson, CancellationToken ct = default) =>
        SaveManualLineupAsync(channelId, JsonNode.Parse(lineupItemsJson)!.AsArray(), append: false, ct);

    private async Task<SavedLineup> SaveManualLineupAsync(string channelId, JsonArray lineup, bool append, CancellationToken ct)
    {
        var body = new JsonObject { ["type"] = "manual", ["lineup"] = lineup, ["append"] = append };
        return await PostAsync<JsonObject, SavedLineup>($"channels/{Uri.EscapeDataString(channelId)}/programming", body, ct)
            .ConfigureAwait(false);
    }

    /// <summary>GET /programs/:id. Null for anything that isn't a playable program (shows and seasons included).</summary>
    public Task<ProgramDetails?> GetProgramAsync(string programId, CancellationToken ct = default) =>
        GetOrNotFoundAsync<ProgramDetails>($"programs/{Uri.EscapeDataString(programId)}", ct);

    /// <summary>
    /// GET /channels/:id/schedule, as Tunarr's JSON: the schedule object, or null without one. Tunarr embeds the
    /// things slots refer to (show, customShow, ...) for display; <see cref="StripSlotDisplayFields"/> removes them.
    /// </summary>
    public async Task<JsonObject?> GetChannelScheduleJsonAsync(string channelId, CancellationToken ct = default)
    {
        var path = $"channels/{Uri.EscapeDataString(channelId)}/schedule";
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, HttpMethod.Get, path, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Length == 0 ? null : JsonNode.Parse(body)?["schedule"] as JsonObject;
    }

    /// <summary>The display-only fields GET /channels/:id/schedule adds to each slot.</summary>
    public static readonly IReadOnlySet<string> SlotDisplayFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "show", "customShow", "fillerList", "smartCollection", "channel", "missingShow", "isMissing",
    };

    /// <summary>A copy of a slot as read, without the fields Tunarr adds for display, ready to save again.</summary>
    public static JsonObject StripSlotDisplayFields(JsonObject slot)
    {
        var copy = (JsonObject)slot.DeepClone();
        foreach (var field in SlotDisplayFields)
            copy.Remove(field);
        return copy;
    }

    /// <summary>
    /// POST /channels/:id/schedule-time-slots or /schedule-slots (by the schedule's type): generates the lineup a
    /// schedule would produce, without saving anything. Tunarr rejects invalid schedules with a 400 and a reason.
    /// </summary>
    public Task<SchedulePreview> PreviewScheduleAsync(string channelId, JsonObject schedule, CancellationToken ct = default)
    {
        var route = (string?)schedule["type"] == "time" ? "schedule-time-slots" : "schedule-slots";
        return PostAsync<JsonObject, SchedulePreview>($"channels/{Uri.EscapeDataString(channelId)}/{route}",
            new JsonObject { ["schedule"] = schedule.DeepClone() }, ct);
    }

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

    /// <summary>GET /custom-shows</summary>
    public async Task<IReadOnlyList<CustomShow>> GetCustomShowsAsync(CancellationToken ct = default) =>
        await GetAsync<List<CustomShow>>("custom-shows", ct).ConfigureAwait(false);

    /// <summary>GET /filler-lists</summary>
    public async Task<IReadOnlyList<FillerList>> GetFillerListsAsync(CancellationToken ct = default) =>
        await GetAsync<List<FillerList>>("filler-lists", ct).ConfigureAwait(false);

    /// <summary>GET /smart_collections</summary>
    public async Task<IReadOnlyList<SmartCollection>> GetSmartCollectionsAsync(CancellationToken ct = default) =>
        await GetAsync<List<SmartCollection>>("smart_collections", ct).ConfigureAwait(false);

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
