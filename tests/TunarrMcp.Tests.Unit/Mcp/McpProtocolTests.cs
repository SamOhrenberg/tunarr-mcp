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
