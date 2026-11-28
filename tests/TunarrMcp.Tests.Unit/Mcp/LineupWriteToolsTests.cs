using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TunarrMcp.Tests.Unit.Mcp;

/// <summary>
/// add_to_channel and undo_last_change end to end, against a stubbed Tunarr whose lineup changes when a write
/// lands (WireMock scenarios), with the change log in a temporary directory.
/// </summary>
public sealed class LineupWriteToolsTests(TestAppFactory factory) : IClassFixture<TestAppFactory>, IAsyncDisposable
{
    private const string ChannelId = "c99";
    private const string LineupPath = "/api/channels/c99/programming";
    private const string Scenario = "lineup";

    private readonly WireMockServer _tunarr = WireMockServer.Start();
    private readonly string _data = Directory.CreateTempSubdirectory("tunarr-mcp-changes-").FullName;
    private WebApplicationFactory<Program>? _app;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        _tunarr.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_data, recursive: true);
    }

    private async Task<McpClient> ConnectAsync(string? writableChannels = null)
    {
        _app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Tunarr:BaseUrl", _tunarr.Url + "/api/");
            b.UseSetting("Tools:Mode", "write");
            b.UseSetting("Data:Path", _data);
            if (writableChannels is not null)
                b.UseSetting("Tools:WritableChannels", writableChannels);
        });
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_app.Server.BaseAddress, "/mcp") },
            _app.CreateClient(), ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private void Get(string path, string body, int status = 200) =>
        _tunarr.Given(Request.Create().WithPath("/api/" + path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

    private static string Lineup(params (string Id, int Ms)[] items) =>
        "{\"name\":\"MCP Sandbox\",\"number\":99,\"totalPrograms\":" + items.Length + ",\"programs\":{},\"lineup\":[" +
        string.Join(",", items.Select(i => $$"""{"type":"content","id":"{{i.Id}}","durationMs":{{i.Ms}},"duration":{{i.Ms}}}""")) + "]}";

    /// <summary>A sandbox channel holding one movie, plus two episodes that can be added to it.</summary>
    private void StubSandbox(string? schedule = null)
    {
        Get("channels", $$"""[{"id":"{{ChannelId}}","number":99,"name":"MCP Sandbox","duration":0,"programCount":1,"startTime":0,"stealth":false}]""");
        _tunarr.Given(Request.Create().WithPath($"/api/channels/{ChannelId}/schedule").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(schedule ?? ""));
        Get("programs/e1", """{"uuid":"e1","type":"episode","title":"Magic Xylophone","episodeNumber":1,"duration":438016,"state":"ok","show":{"title":"Bluey (2018)"},"season":{"index":1}}""");
        Get("programs/e2", """{"uuid":"e2","type":"episode","title":"Hospital","episodeNumber":2,"duration":438016,"state":"ok","show":{"title":"Bluey (2018)"},"season":{"index":1}}""");
        Get("programs/gone", """{"uuid":"gone","type":"episode","title":"Lost","duration":1000,"state":"missing"}""");
        Get("programs/show1", "", status: 404);

        // The lineup before the write, after it, and after undo restores it.
        _tunarr.Given(Request.Create().WithPath(LineupPath).UsingGet()).InScenario(Scenario)
            .RespondWith(Response.Create().WithBody(Lineup(("m1", 7_471_260))));
        _tunarr.Given(Request.Create().WithPath(LineupPath).UsingPost().WithBody(b => b!.Contains("\"append\":true", StringComparison.Ordinal)))
            .InScenario(Scenario).WillSetStateTo("added")
            .RespondWith(Response.Create().WithBody(Lineup(("m1", 7_471_260), ("e1", 438_016), ("e2", 438_016))));
        _tunarr.Given(Request.Create().WithPath(LineupPath).UsingGet()).InScenario(Scenario).WhenStateIs("added")
            .RespondWith(Response.Create().WithBody(Lineup(("m1", 7_471_260), ("e1", 438_016), ("e2", 438_016))));
        _tunarr.Given(Request.Create().WithPath(LineupPath).UsingPost().WithBody(b => b!.Contains("\"append\":false", StringComparison.Ordinal)))
            .InScenario(Scenario).WillSetStateTo("restored")
            .RespondWith(Response.Create().WithBody(Lineup(("m1", 7_471_260))));
        _tunarr.Given(Request.Create().WithPath(LineupPath).UsingGet()).InScenario(Scenario).WhenStateIs("restored")
            .RespondWith(Response.Create().WithBody(Lineup(("m1", 7_471_260))));
    }

    private static Task<CallToolResult> Add(McpClient client, params string[] ids) =>
        client.CallToolAsync("add_to_channel", new Dictionary<string, object?> { ["channel"] = "99", ["programIds"] = ids }, cancellationToken: Ct).AsTask();

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private JsonElement[] Posts() => _tunarr.LogEntries
        .Where(e => e.RequestMessage!.Method == "POST")
        .Select(e => { using var json = JsonDocument.Parse(e.RequestMessage!.Body!); return json.RootElement.Clone(); })
        .ToArray();

    [Fact]
    public async Task Add_appends_the_programs_with_their_real_durations()
    {
        StubSandbox();
        await using var client = await ConnectAsync();

        var result = await Add(client, "e1", "e2");

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(Text(result));
        Assert.Equal(["Bluey (2018) S1E1 Magic Xylophone", "Bluey (2018) S1E2 Hospital"],
            json.RootElement.GetProperty("added").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(3, json.RootElement.GetProperty("totalItems").GetInt32());

        var post = Assert.Single(Posts());
        Assert.Equal("manual", post.GetProperty("type").GetString());
        Assert.True(post.GetProperty("append").GetBoolean());
        var item = post.GetProperty("lineup")[0];
        Assert.Equal("content", item.GetProperty("type").GetString());
        Assert.Equal("e1", item.GetProperty("id").GetString());
        Assert.Equal(438016, item.GetProperty("duration").GetDouble());
    }

    [Fact]
    public async Task Undo_restores_the_lineup_as_it_was_and_then_has_nothing_left()
    {
        StubSandbox();
        await using var client = await ConnectAsync();
        await Add(client, "e1", "e2");

        var undo = await client.CallToolAsync("undo_last_change", cancellationToken: Ct);

        Assert.NotEqual(true, undo.IsError);
        using (var json = JsonDocument.Parse(Text(undo)))
        {
            Assert.Equal(1, json.RootElement.GetProperty("totalItems").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("moreToUndo").GetInt32());
        }

        var restore = Posts()[1];
        Assert.False(restore.GetProperty("append").GetBoolean());
        var items = restore.GetProperty("lineup");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("m1", items[0].GetProperty("id").GetString()); // the snapshot, sent back as read

        var again = await client.CallToolAsync("undo_last_change", new Dictionary<string, object?> { ["channel"] = "99" }, cancellationToken: Ct);
        Assert.True(again.IsError);
        Assert.Contains("nothing to undo", Text(again), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_refuses_when_the_channel_changed_since_unless_forced()
    {
        StubSandbox();
        await using var client = await ConnectAsync();
        await Add(client, "e1", "e2");
        // Someone edits the channel in Tunarr afterwards.
        _tunarr.Given(Request.Create().WithPath(LineupPath).UsingGet()).InScenario(Scenario).WhenStateIs("added")
            .RespondWith(Response.Create().WithBody(Lineup(("m1", 7_471_260), ("e1", 438_016))));

        var refused = await client.CallToolAsync("undo_last_change", cancellationToken: Ct);

        Assert.True(refused.IsError);
        Assert.Contains("force: true", Text(refused), StringComparison.Ordinal);
        Assert.Single(Posts()); // nothing was written

        var forced = await client.CallToolAsync("undo_last_change", new Dictionary<string, object?> { ["force"] = true }, cancellationToken: Ct);
        Assert.NotEqual(true, forced.IsError);
    }

    [Fact]
    public async Task Add_rejects_shows_and_missing_files_without_writing()
    {
        StubSandbox();
        await using var client = await ConnectAsync();

        var result = await Add(client, "e1", "show1", "gone");

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("Not episodes or movies: show1", text, StringComparison.Ordinal);
        Assert.Contains("Can't be played", text, StringComparison.Ordinal);
        Assert.Contains("Nothing was added.", text, StringComparison.Ordinal);
        Assert.Empty(Posts());
    }

    [Fact]
    public async Task Add_to_a_slot_scheduled_channel_needs_explicit_permission()
    {
        StubSandbox(schedule: """{"schedule":{"type":"random","slots":[]}}""");
        await using var client = await ConnectAsync();

        var refused = await Add(client, "e1");
        var allowed = await client.CallToolAsync("add_to_channel",
            new Dictionary<string, object?> { ["channel"] = "99", ["programIds"] = new[] { "e1" }, ["allowOnScheduledChannel"] = true },
            cancellationToken: Ct);

        Assert.True(refused.IsError);
        Assert.Contains("random slots", Text(refused), StringComparison.Ordinal);
        Assert.NotEqual(true, allowed.IsError);
        Assert.Contains("regenerate", Text(allowed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Channels_outside_the_allowlist_are_read_only()
    {
        StubSandbox();
        await using var client = await ConnectAsync(writableChannels: "7, some-other-id");

        var result = await Add(client, "e1");

        Assert.True(result.IsError);
        Assert.Contains("only allows 7, some-other-id", Text(result), StringComparison.Ordinal);
        Assert.Empty(Posts());
    }

    [Fact]
    public async Task Allowlist_accepts_the_channel_by_id()
    {
        StubSandbox();
        await using var client = await ConnectAsync(writableChannels: ChannelId.ToUpperInvariant());

        Assert.NotEqual(true, (await Add(client, "e1")).IsError);
    }

    [Fact]
    public async Task Write_tools_are_hidden_in_read_mode()
    {
        _app = factory.WithWebHostBuilder(b => b.UseSetting("Tunarr:BaseUrl", _tunarr.Url + "/api/"));
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_app.Server.BaseAddress, "/mcp") },
            _app.CreateClient(), ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);

        var names = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToArray();

        Assert.DoesNotContain("add_to_channel", names);
        Assert.DoesNotContain("undo_last_change", names);
        Assert.Contains("list_channels", names);
    }
}
