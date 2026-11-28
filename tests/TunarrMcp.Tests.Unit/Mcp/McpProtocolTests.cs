using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TunarrMcp.Tests.Unit.Mcp;

/// <summary>Real MCP client against the in-process host, with Tunarr stubbed by WireMock.</summary>
public sealed class McpProtocolTests(TestAppFactory factory) : IClassFixture<TestAppFactory>, IAsyncDisposable
{
    private readonly WireMockServer _tunarr = WireMockServer.Start();
    private WebApplicationFactory<Program>? _app;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        _tunarr.Dispose();
    }

    private async Task<McpClient> ConnectAsync()
    {
        _app = factory.WithWebHostBuilder(b => b.UseSetting("Tunarr:BaseUrl", _tunarr.Url + "/api/"));
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_app.Server.BaseAddress, "/mcp") },
            _app.CreateClient(), ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private void Stub(string path, string body, int status = 200) =>
        _tunarr.Given(Request.Create().WithPath("/api/" + path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

    [Fact]
    public async Task Tools_list_advertises_system_status_as_read_only()
    {
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        var tool = Assert.Single(tools, t => t.Name == "system_status");
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Equal("Tunarr system status", tool.ProtocolTool.Title);
    }

    [Fact]
    public async Task System_status_returns_shaped_json()
    {
        Stub("version", """{"tunarr":"2026.11.2","ffmpeg":"8.1.2"}""");
        Stub("system/health", """{"FfmpegVersion":{"type":"healthy"}}""");
        Stub("tasks", """[{"id":"BackupTask","name":"BackupTask","description":"Backs up Grey's Anatomy & Pokémon","scheduledTasks":[]}]""");
        Stub("guide/status", """{"lastUpdate":{},"guideTimes":{},"channelIds":[]}""");
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("system_status", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("Grey's Anatomy & Pokémon", text, StringComparison.Ordinal); // not ' / & / é
        using var json = JsonDocument.Parse(text);
        Assert.Equal("2026.11.2", json.RootElement.GetProperty("tunarrVersion").GetString());
        Assert.True(json.RootElement.GetProperty("healthy").GetBoolean());
        Assert.Equal("BackupTask", json.RootElement.GetProperty("tasks")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task List_channels_returns_each_channel_with_its_programming()
    {
        Stub("channels", """
            [{"id":"c1","number":1,"name":"A.S. Movies","duration":3600000,"programCount":2,"startTime":0,"stealth":false},
             {"id":"c5","number":5,"name":"Sam-O-Rama","duration":7200000,"programCount":4,"startTime":0,"stealth":false}]
            """);
        _tunarr.Given(Request.Create().WithPath("/api/channels/c1/schedule").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        Stub("channels/c5/schedule", """{"schedule":{"type":"random","slots":[]}}""");
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("list_channels", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        var channels = json.RootElement.GetProperty("channels");
        Assert.Equal("manual", channels[0].GetProperty("programming").GetString());
        Assert.Equal("random_slots", channels[1].GetProperty("programming").GetString());
        Assert.Equal("c5", channels[1].GetProperty("id").GetString());
    }

    private const string MediaSources = """[{"id":"src1","name":"Jellyfin","type":"jellyfin","libraries":[]}]""";

    private static string ToolText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task Search_library_names_the_source_of_each_result()
    {
        Stub("media-sources", MediaSources);
        _tunarr.Given(Request.Create().WithPath("/api/programs/search").UsingPost())
            .RespondWith(Response.Create().WithBody("""
                {"results":[{"uuid":"s1","type":"show","title":"Bluey (2018)","year":2018,"mediaSourceId":"src1","childCount":3,"grandchildCount":154}],
                 "page":1,"totalPages":1,"totalHits":1}
                """));
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("search_library", new Dictionary<string, object?> { ["query"] = "bluey", ["source"] = "jellyfin" },
            cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(ToolText(result));
        var item = json.RootElement.GetProperty("results")[0];
        Assert.Equal("Jellyfin", item.GetProperty("source").GetString());
        Assert.Equal(154, item.GetProperty("episodes").GetInt32());
        Assert.False(item.TryGetProperty("minutes", out _)); // nulls are left out
    }

    [Fact]
    public async Task Search_library_rejects_an_unknown_source_by_listing_the_real_ones()
    {
        Stub("media-sources", MediaSources);
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("search_library", new Dictionary<string, object?> { ["source"] = "plex" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("Sources: Jellyfin.", ToolText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_show_lists_the_episodes_of_a_season()
    {
        Stub("media-sources", MediaSources);
        Stub("program_groupings/s1", """
            {"uuid":"s1","type":"show","title":"Bluey (2018)","mediaSourceId":"src1","grandchildCount":2,
             "seasons":[{"uuid":"se1","index":1,"title":"Season 1","childCount":2}]}
            """);
        Stub("programs/se1/children", """
            {"total":2,"size":2,"result":{"type":"episode","programs":[
              {"uuid":"e2","title":"Hospital","episodeNumber":2,"duration":438016,"state":"ok"},
              {"uuid":"e1","title":"Magic Xylophone","episodeNumber":1,"duration":438016,"state":"ok"}]}}
            """);
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("get_show", new Dictionary<string, object?> { ["showId"] = "s1", ["season"] = 1 }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(ToolText(result));
        var episodes = json.RootElement.GetProperty("season").GetProperty("episodes");
        Assert.Equal("e1", episodes[0].GetProperty("id").GetString());
        Assert.Equal("Hospital", episodes[1].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Get_show_with_a_movie_id_says_how_to_find_a_show()
    {
        Stub("media-sources", MediaSources);
        Stub("program_groupings/m1", "", status: 404);
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("get_show", new Dictionary<string, object?> { ["showId"] = "m1" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("Use search_library", ToolText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_collections_returns_all_three_kinds()
    {
        Stub("custom-shows", """[{"id":"cs1","name":"TRCPD Movies","contentCount":9,"schedulableContentCount":9,"totalDuration":67703816,"isSyncing":false}]""");
        Stub("filler-lists", """[{"id":"f1","name":"Toonami Filler","contentCount":50}]""");
        Stub("smart_collections", "[]");
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("list_collections", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(ToolText(result));
        Assert.Equal("cs1", json.RootElement.GetProperty("customShows")[0].GetProperty("id").GetString());
        Assert.Equal("f1", json.RootElement.GetProperty("fillerLists")[0].GetProperty("id").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("smartCollections").GetArrayLength());
    }

    private const string TwoChannels = """
        [{"id":"c3","number":3,"name":"Animation Station","duration":0,"programCount":2,"startTime":0,"stealth":false},
         {"id":"c5","number":5,"name":"Sam-O-Rama","duration":0,"programCount":1,"startTime":0,"stealth":false}]
        """;

    [Fact]
    public async Task Get_channel_schedule_describes_random_slots_by_channel_number()
    {
        Stub("channels", TwoChannels);
        Stub("filler-lists", """[{"id":"f1","name":"Toonami Filler","contentCount":50}]""");
        // Trimmed from a real response: the slot, plus the show Tunarr embeds for display.
        Stub("channels/c5/schedule", """
            {"schedule":{"type":"random","flexPreference":"distribute","maxDays":7,"padMs":300000,"padStyle":"slot",
              "timeZoneOffset":360,"randomDistribution":"weighted","lockWeights":false,
              "slots":[{"id":"58dbdd2b","linkMode":"continue","rerunOverflow":"flex",
                "filler":[{"types":["pre"],"fillerListId":"f1","fillerOrder":"shuffle_prefer_short"}],
                "cooldownMs":1800000,"durationSpec":{"type":"dynamic","programCount":1},"weight":9.99,"index":0,
                "order":"ordered_shuffle","direction":"asc","type":"show","showId":"s1","seasonFilter":[],"seasonExcludeFilter":[9],
                "show":{"uuid":"s1","type":"show","title":"Scrubs","year":2001,"childCount":9,"genres":[]}}]},
             "rebuildEachCycle":false}
            """);
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("get_channel_schedule", new Dictionary<string, object?> { ["channel"] = "5" }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(ToolText(result));
        Assert.Equal("random_slots", json.RootElement.GetProperty("programming").GetString());
        var slot = json.RootElement.GetProperty("slots")[0];
        Assert.Equal("Scrubs", slot.GetProperty("title").GetString());
        Assert.Equal(9, slot.GetProperty("exceptSeasons")[0].GetInt32());
        Assert.Equal("Toonami Filler", slot.GetProperty("filler")[0].GetProperty("name").GetString());
        Assert.Equal("58dbdd2b", slot.GetProperty("slotId").GetString());
    }

    [Fact]
    public async Task Get_channel_lineup_finds_the_channel_by_name()
    {
        Stub("channels", TwoChannels);
        _tunarr.Given(Request.Create().WithPath("/api/channels/c3/schedule").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        _tunarr.Given(Request.Create().WithPath("/api/channels/c3/programming").WithParam("offset", "0").WithParam("limit", "50").UsingGet())
            .RespondWith(Response.Create().WithBody("""
                {"name":"Animation Station","number":3,"totalPrograms":2,"startTimeOffsets":[0,1444075],
                 "lineup":[{"type":"content","id":"p1","durationMs":1444075,"duration":1444075},{"type":"flex","duration":600000}],
                 "programs":{"p1":{"type":"content","id":"p1","duration":1444075,
                   "program":{"uuid":"p1","type":"episode","title":"Welcome to Republic City","episodeNumber":1,"state":"ok",
                     "show":{"uuid":"s1","title":"The Legend of Korra"},"season":{"uuid":"se1","index":1}}}}}
                """));
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("get_channel_lineup", new Dictionary<string, object?> { ["channel"] = "animation station" },
            cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(ToolText(result));
        Assert.Equal("manual", json.RootElement.GetProperty("programming").GetString());
        var items = json.RootElement.GetProperty("items");
        Assert.Equal("The Legend of Korra", items[0].GetProperty("show").GetString());
        Assert.Equal("flex", items[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Unknown_channel_lists_the_real_ones()
    {
        Stub("channels", TwoChannels);
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("get_channel_schedule", new Dictionary<string, object?> { ["channel"] = "9" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("Channels: 3 Animation Station, 5 Sam-O-Rama.", ToolText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_schedule_sends_the_built_schedule_to_the_time_slot_route()
    {
        Stub("channels", TwoChannels);
        _tunarr.Given(Request.Create().WithPath("/api/channels/c3/schedule").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        _tunarr.Given(Request.Create().WithPath("/api/channels/c3/schedule-time-slots").UsingPost())
            .RespondWith(Response.Create().WithBody("""
                {"startTime":1792900800000,"seed":[1],"discardCount":0,
                 "lineup":[{"type":"content","id":"p1","duration":438016},{"type":"flex","duration":1361984}],
                 "programs":{"p1":{"type":"content","id":"p1","duration":438016,"program":{"uuid":"p1","type":"episode","title":"Magic Xylophone","episodeNumber":1,"show":{"title":"Bluey (2018)"},"season":{"index":1}}}}}
                """));
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("preview_schedule", new Dictionary<string, object?>
        {
            ["channel"] = "3",
            ["schedule"] = new { type = "time", padMinutes = 30, slots = new object[] { new { plays = "show", id = "s1", at = "07:00" } } },
        }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(ToolText(result));
        Assert.Equal("time_slots", json.RootElement.GetProperty("programming").GetString());
        Assert.NotEqual(0, json.RootElement.GetProperty("upcoming").GetArrayLength());

        var sent = _tunarr.LogEntries.Single(e => e.RequestMessage!.Method == "POST").RequestMessage!.Body!;
        using var body = JsonDocument.Parse(sent);
        var schedule = body.RootElement.GetProperty("schedule");
        Assert.Equal(1_800_000, schedule.GetProperty("padMs").GetInt32());
        Assert.Equal(7 * 3_600_000, schedule.GetProperty("slots")[0].GetProperty("startTime").GetInt32());
    }

    [Fact]
    public async Task Preview_schedule_passes_tunarrs_reason_for_rejecting_a_schedule()
    {
        Stub("channels", TwoChannels);
        _tunarr.Given(Request.Create().WithPath("/api/channels/c3/schedule").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        _tunarr.Given(Request.Create().WithPath("/api/channels/c3/schedule-slots").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("Slot 0 references playlist cs1, which has no content to schedule"));
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("preview_schedule", new Dictionary<string, object?>
        {
            ["channel"] = "3",
            ["schedule"] = new { type = "random", slots = new object[] { new { plays = "custom_show", id = "cs1" } } },
        }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("has no content to schedule", ToolText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tunarr_error_is_reported_to_the_model()
    {
        // Only /version fails. 404 is not retried by the resilience handler, so the test stays fast.
        Stub("version", """{"message":"nope"}""", status: 404);
        Stub("system/health", "{}");
        Stub("tasks", "[]");
        Stub("guide/status", """{"lastUpdate":{},"guideTimes":{},"channelIds":[]}""");
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("system_status", cancellationToken: Ct);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("Tunarr GET /version returned 404", text, StringComparison.Ordinal);
        Assert.Contains("nope", text, StringComparison.Ordinal);
    }
}
