using System.Net;
using System.Text.Json;
using TunarrMcp.Tunarr;
using TunarrMcp.Tunarr.Models;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TunarrMcp.Tests.Unit.Tunarr;

public sealed class TunarrClientTests : IDisposable
{
    private readonly WireMockServer _tunarr = WireMockServer.Start();
    private readonly HttpClient _http;
    private readonly TunarrClient _client;

    public TunarrClientTests()
    {
        _http = new HttpClient { BaseAddress = new Uri(_tunarr.Url + "/api/") };
        _client = new TunarrClient(_http);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tunarr.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Version_is_read_from_api_root()
    {
        _tunarr.Given(Request.Create().WithPath("/api/version").UsingGet())
            .RespondWith(Response.Create().WithBody("""{"tunarr":"2026.11.2","ffmpeg":"8.1.2-etv.8","etvNext":"0.2.5+linux-x64"}"""));

        var version = await _client.GetVersionAsync(Ct);

        Assert.Equal("2026.11.2", version.Tunarr);
        Assert.Equal("8.1.2-etv.8", version.Ffmpeg);
        Assert.Equal("0.2.5+linux-x64", version.EtvNext);
    }

    [Fact]
    public async Task Health_maps_each_check()
    {
        _tunarr.Given(Request.Create().WithPath("/api/system/health").UsingGet())
            .RespondWith(Response.Create().WithBody("""
                {"FfmpegVersion":{"type":"healthy"},
                 "MissingSeasonNumbers":{"type":"warning","context":"3 shows have no season numbers"}}
                """));

        var health = await _client.GetHealthAsync(Ct);

        Assert.True(health["FfmpegVersion"].IsHealthy);
        Assert.False(health["MissingSeasonNumbers"].IsHealthy);
        Assert.Equal("3 shows have no season numbers", health["MissingSeasonNumbers"].Context);
    }

    [Fact]
    public async Task Channels_are_read_with_the_fields_tools_use()
    {
        _tunarr.Given(Request.Create().WithPath("/api/channels").UsingGet())
            .RespondWith(Response.Create().WithBody("""
                [{"id":"ec085630-c12f-4535-90e2-b4050fdc472c","number":5,"name":"Sam-O-Rama","groupTitle":"tunarr",
                  "duration":694500197.98,"programCount":815,"startTime":1792900800000,"stealth":false,
                  "onDemand":{"enabled":false},"streamMode":"hls","sessions":[]}]
                """));

        var channel = Assert.Single(await _client.GetChannelsAsync(Ct));

        Assert.Equal(5, channel.Number);
        Assert.Equal("Sam-O-Rama", channel.Name);
        Assert.Equal(815, channel.ProgramCount);
        Assert.Equal(1792900800000, channel.StartTime);
        Assert.False(channel.OnDemand?.Enabled);
    }

    [Fact]
    public async Task Channel_schedule_type_is_read()
    {
        _tunarr.Given(Request.Create().WithPath("/api/channels/c1/schedule").UsingGet())
            .RespondWith(Response.Create().WithBody("""{"schedule":{"type":"random","maxDays":7,"slots":[]},"rebuildEachCycle":false}"""));

        var schedule = await _client.GetChannelScheduleAsync("c1", Ct);

        Assert.Equal("random", schedule?.Schedule?.Type);
        Assert.False(schedule?.RebuildEachCycle);
    }

    [Fact]
    public async Task Channel_without_a_schedule_is_null()
    {
        // What Tunarr really sends for a hand-built lineup: 200 with no body.
        _tunarr.Given(Request.Create().WithPath("/api/channels/c1/schedule").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        Assert.Null(await _client.GetChannelScheduleAsync("c1", Ct));
    }

    [Fact]
    public async Task Empty_body_where_one_is_required_is_a_TunarrApiException()
    {
        _tunarr.Given(Request.Create().WithPath("/api/channels").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var e = await Assert.ThrowsAsync<TunarrApiException>(() => _client.GetChannelsAsync(Ct));

        Assert.Contains("empty body", e.Message, StringComparison.Ordinal);
    }

    private const string EmptySearch = """{"results":[],"page":1,"totalPages":0,"totalHits":0}""";

    private JsonElement LastRequestBody()
    {
        var body = Assert.Single(_tunarr.LogEntries).RequestMessage!.Body;
        using var json = JsonDocument.Parse(body!);
        return json.RootElement.Clone();
    }

    [Fact]
    public async Task Text_search_matches_titles_ranked_by_relevance()
    {
        _tunarr.Given(Request.Create().WithPath("/api/programs/search").UsingPost())
            .RespondWith(Response.Create().WithBody(EmptySearch));

        await _client.SearchProgramsAsync(new LibrarySearch("bluey", ["show", "movie"], null, "src1", Page: 2), Ct);

        var body = LastRequestBody();
        var query = body.GetProperty("query");
        Assert.Equal("bluey", query.GetProperty("query").GetString());
        Assert.Equal(["title"], query.GetProperty("restrictSearchTo").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0, query.GetProperty("sort").GetArrayLength()); // [] means relevance; leaving it out sorts by title
        var spec = query.GetProperty("filter").GetProperty("fieldSpec");
        Assert.Equal("value", query.GetProperty("filter").GetProperty("type").GetString());
        Assert.Equal("type", spec.GetProperty("key").GetString());
        Assert.Equal("in", spec.GetProperty("op").GetString());
        Assert.Equal(["show", "movie"], spec.GetProperty("value").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("src1", body.GetProperty("mediaSourceId").GetString());
        Assert.Equal(2, body.GetProperty("page").GetInt32()); // text queries page from 1
        Assert.True(body.GetProperty("expandParents").GetBoolean());
    }

    [Fact]
    public async Task Browsing_pages_from_zero_and_combines_filters_with_and()
    {
        _tunarr.Given(Request.Create().WithPath("/api/programs/search").UsingPost())
            .RespondWith(Response.Create().WithBody(EmptySearch));

        await _client.SearchProgramsAsync(new LibrarySearch(null, ["movie"], "Animation", null, Page: 1), Ct);

        var body = LastRequestBody();
        var query = body.GetProperty("query");
        Assert.Equal(0, body.GetProperty("page").GetInt32());
        // Tunarr's schema rejects null for these, so they must be absent rather than null.
        Assert.False(query.TryGetProperty("query", out _));
        Assert.False(query.TryGetProperty("restrictSearchTo", out _));
        Assert.False(query.TryGetProperty("sort", out _));
        Assert.False(body.TryGetProperty("mediaSourceId", out _));
        var filter = query.GetProperty("filter");
        Assert.Equal("op", filter.GetProperty("type").GetString());
        Assert.Equal("and", filter.GetProperty("op").GetString());
        Assert.Equal("genres.name", filter.GetProperty("children")[1].GetProperty("fieldSpec").GetProperty("key").GetString());
    }

    [Fact]
    public async Task Search_hits_carry_episode_parents()
    {
        _tunarr.Given(Request.Create().WithPath("/api/programs/search").UsingPost())
            .RespondWith(Response.Create().WithBody("""
                {"results":[{"uuid":"e1","type":"episode","title":"The Beach","year":2018,"mediaSourceId":"src1",
                  "duration":438058,"episodeNumber":26,"show":{"uuid":"s1","title":"Bluey (2018)"},"season":{"uuid":"se1","index":1}}],
                 "page":1,"totalPages":1,"totalHits":1}
                """));

        var hit = Assert.Single((await _client.SearchProgramsAsync(new LibrarySearch("beach", ["episode"], null, null, 1), Ct)).Results);

        Assert.Equal("Bluey (2018)", hit.Show?.Title);
        Assert.Equal(1, hit.Season?.Index);
        Assert.Equal(26, hit.EpisodeNumber);
    }

    [Fact]
    public async Task Unknown_show_is_null()
    {
        _tunarr.Given(Request.Create().WithPath("/api/program_groupings/nope").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        Assert.Null(await _client.GetShowAsync("nope", Ct));
    }

    [Fact]
    public async Task Season_episodes_are_read()
    {
        _tunarr.Given(Request.Create().WithPath("/api/programs/se1/children").UsingGet())
            .RespondWith(Response.Create().WithBody("""
                {"total":1,"size":1,"result":{"type":"episode","programs":[
                  {"uuid":"e1","type":"episode","title":"Magic Xylophone","episodeNumber":1,"duration":438016,"state":"ok"}]}}
                """));

        var episode = Assert.Single((await _client.GetSeasonEpisodesAsync("se1", Ct)).Result.Programs);

        Assert.Equal("Magic Xylophone", episode.Title);
        Assert.Equal(438016, episode.Duration);
    }

    [Fact]
    public async Task Non_success_status_becomes_TunarrApiException_with_detail()
    {
        _tunarr.Given(Request.Create().WithPath("/api/version").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"message":"Not found"}"""));

        var e = await Assert.ThrowsAsync<TunarrApiException>(() => _client.GetVersionAsync(Ct));

        Assert.Equal(HttpStatusCode.NotFound, e.StatusCode);
        Assert.Equal("version", e.Path);
        Assert.Contains("Not found", e.Message, StringComparison.Ordinal);
    }
}
