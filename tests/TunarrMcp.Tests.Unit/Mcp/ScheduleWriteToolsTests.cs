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
/// set_channel_schedule and undoing it, against a stubbed Tunarr whose lineup and schedule change as writes land
/// (WireMock scenarios), with the change log in a temporary directory.
/// </summary>
public sealed class ScheduleWriteToolsTests(TestAppFactory factory) : IClassFixture<TestAppFactory>, IAsyncDisposable
{
    private const string Programming = "/api/channels/c99/programming";
    private const string Schedule = "/api/channels/c99/schedule";
    private const string Scenario = "channel";

    private const string Bluey = """{"plays":"show","id":"s1","at":"07:00"}""";

    private readonly WireMockServer _tunarr = WireMockServer.Start();
    private readonly string _data = Directory.CreateTempSubdirectory("tunarr-mcp-schedules-").FullName;
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

    private static string Lineup(params string[] ids) =>
        "{\"name\":\"MCP Sandbox\",\"number\":99,\"totalPrograms\":" + ids.Length + ",\"programs\":{},\"lineup\":[" +
        string.Join(",", ids.Select(id => $$"""{"type":"content","id":"{{id}}","duration":1000}""")) + "]}";

    private const string SavedSchedule = """
        {"schedule":{"type":"random","flexPreference":"distribute","maxDays":7,"padMs":300000,"padStyle":"slot","randomDistribution":"weighted","lockWeights":false,
          "slots":[{"id":"slot-1","type":"show","showId":"s9","order":"shuffle","direction":"asc","seasonFilter":[],"seasonExcludeFilter":[],
            "weight":1,"cooldownMs":0,"durationSpec":{"type":"dynamic","programCount":1},"index":0,"show":{"title":"Scrubs"}}]},
         "rebuildEachCycle":false}
        """;

    /// <summary>
    /// Channel 99 before: either hand-built (one program, no schedule) or random-slot scheduled. A schedule save
    /// moves it to state "saved"; after that the undo writes move it to "restored".
    /// </summary>
    private void StubChannel(bool scheduled, int programCount = 1)
    {
        _tunarr.Given(Request.Create().WithPath("/api/channels").UsingGet()).RespondWith(Response.Create().WithBody(
            $$"""[{"id":"c99","number":99,"name":"MCP Sandbox","duration":1000,"programCount":{{programCount}},"startTime":0,"stealth":false}]"""));

        _tunarr.Given(Request.Create().WithPath(Schedule).UsingGet()).InScenario(Scenario)
            .RespondWith(Response.Create().WithBody(scheduled ? SavedSchedule : ""));
        _tunarr.Given(Request.Create().WithPath(Programming).UsingGet()).InScenario(Scenario)
            .RespondWith(Response.Create().WithBody(Lineup("old")));

        _tunarr.Given(Request.Create().WithPath(Programming).UsingPost().WithBody(b => b!.Contains("\"type\":\"time\"", StringComparison.Ordinal)))
            .InScenario(Scenario).WillSetStateTo("saved")
            .RespondWith(Response.Create().WithBody(Lineup("new1", "new2")));
        _tunarr.Given(Request.Create().WithPath(Programming).UsingGet()).InScenario(Scenario).WhenStateIs("saved")
            .RespondWith(Response.Create().WithBody(Lineup("new1", "new2")));

        // Undo: the old schedule saved back (random) or deleted, then the old lineup replaced in.
        _tunarr.Given(Request.Create().WithPath(Programming).UsingPost().WithBody(b => b!.Contains("\"type\":\"random\"", StringComparison.Ordinal)))
            .RespondWith(Response.Create().WithBody(Lineup("regenerated")));
        _tunarr.Given(Request.Create().WithPath(Schedule).UsingDelete()).RespondWith(Response.Create().WithStatusCode(204));
        _tunarr.Given(Request.Create().WithPath(Programming).UsingPost().WithBody(b => b!.Contains("\"type\":\"manual\"", StringComparison.Ordinal)))
            .InScenario(Scenario).WillSetStateTo("restored")
            .RespondWith(Response.Create().WithBody(Lineup("old")));
    }

    private static Task<CallToolResult> Save(McpClient client, string schedule, bool replaceHandBuilt = false) =>
        client.CallToolAsync("set_channel_schedule", new Dictionary<string, object?>
        {
            ["channel"] = "99",
            ["schedule"] = JsonSerializer.Deserialize<JsonElement>(schedule),
            ["replaceHandBuiltLineup"] = replaceHandBuilt,
        }, cancellationToken: Ct).AsTask();

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    /// <summary>Every write sent to Tunarr, in order, as "METHOD path type".</summary>
    private string[] Writes() => _tunarr.LogEntries
        .Where(e => e.RequestMessage!.Method is "POST" or "DELETE")
        .Select(e =>
        {
            var type = "";
            if (e.RequestMessage!.Body is { Length: > 0 } body)
            {
                using var json = JsonDocument.Parse(body);
                type = " " + json.RootElement.GetProperty("type").GetString();
            }

            return $"{e.RequestMessage.Method} {e.RequestMessage.Path}{type}";
        })
        .ToArray();

    private JsonElement WriteBody(int index)
    {
        var entry = _tunarr.LogEntries.Where(e => e.RequestMessage!.Method == "POST").ElementAt(index);
        using var json = JsonDocument.Parse(entry.RequestMessage!.Body!);
        return json.RootElement.Clone();
    }

    [Fact]
    public async Task Save_sends_the_built_schedule_with_no_program_pool()
    {
        StubChannel(scheduled: true);
        await using var client = await ConnectAsync();

        var result = await Save(client, $$"""{"type":"time","padMinutes":30,"slots":[{{Bluey}}]}""");

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(Text(result));
        Assert.Equal("time_slots", json.RootElement.GetProperty("programming").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("generatedItems").GetInt32());

        var body = WriteBody(0);
        Assert.Equal("time", body.GetProperty("type").GetString());
        Assert.Equal(0, body.GetProperty("programs").GetArrayLength());
        Assert.Equal(1_800_000, body.GetProperty("schedule").GetProperty("padMs").GetInt32());
        Assert.Equal("s1", body.GetProperty("schedule").GetProperty("slots")[0].GetProperty("showId").GetString());
    }

    [Fact]
    public async Task Undo_puts_the_old_schedule_back_then_the_exact_old_lineup()
    {
        StubChannel(scheduled: true);
        await using var client = await ConnectAsync();
        await Save(client, $$"""{"type":"time","slots":[{{Bluey}}]}""");

        var undo = await client.CallToolAsync("undo_last_change", cancellationToken: Ct);

        Assert.NotEqual(true, undo.IsError);
        Assert.Equal(
            ["POST /api/channels/c99/programming time", "POST /api/channels/c99/programming random", "POST /api/channels/c99/programming manual"],
            Writes());
        var schedule = WriteBody(1).GetProperty("schedule");
        Assert.Equal(300000, schedule.GetProperty("padMs").GetInt32());
        var slot = schedule.GetProperty("slots")[0];
        Assert.Equal("slot-1", slot.GetProperty("id").GetString());
        Assert.False(slot.TryGetProperty("show", out _)); // display fields aren't sent back
        Assert.Equal("old", WriteBody(2).GetProperty("lineup")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Hand_built_channel_needs_permission_and_undo_removes_the_schedule_again()
    {
        StubChannel(scheduled: false);
        await using var client = await ConnectAsync();

        var refused = await Save(client, $$"""{"type":"time","slots":[{{Bluey}}]}""");
        Assert.True(refused.IsError);
        Assert.Contains("replaceHandBuiltLineup: true", Text(refused), StringComparison.Ordinal);
        Assert.Empty(Writes());

        var saved = await Save(client, $$"""{"type":"time","slots":[{{Bluey}}]}""", replaceHandBuilt: true);
        Assert.NotEqual(true, saved.IsError);

        var undo = await client.CallToolAsync("undo_last_change", new Dictionary<string, object?> { ["channel"] = "99" }, cancellationToken: Ct);

        Assert.NotEqual(true, undo.IsError);
        Assert.Equal(
            ["POST /api/channels/c99/programming time", "DELETE /api/channels/c99/schedule", "POST /api/channels/c99/programming manual"],
            Writes());
    }

    [Fact]
    public async Task Empty_hand_built_channel_needs_no_permission()
    {
        StubChannel(scheduled: false, programCount: 0);
        await using var client = await ConnectAsync();

        Assert.NotEqual(true, (await Save(client, $$"""{"type":"time","slots":[{{Bluey}}]}""")).IsError);
    }

    [Fact]
    public async Task Invalid_schedule_writes_nothing_and_records_nothing()
    {
        StubChannel(scheduled: true);
        await using var client = await ConnectAsync();

        var result = await Save(client, """{"type":"time","slots":[{"plays":"show","id":"s1","at":"7am"}]}""");
        var undo = await client.CallToolAsync("undo_last_change", cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("nothing was sent to Tunarr", Text(result), StringComparison.Ordinal);
        Assert.Empty(Writes());
        Assert.Contains("nothing to undo", Text(undo), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Previews_never_touch_the_data_directory()
    {
        // A data path that can't be created on any OS: under a regular file.
        var file = Path.Combine(_data, "not-a-folder");
        await File.WriteAllTextAsync(file, "", Ct);
        StubChannel(scheduled: true);
        _tunarr.Given(Request.Create().WithPath("/api/channels/c99/schedule-time-slots").UsingPost())
            .RespondWith(Response.Create().WithBody("""{"startTime":1,"lineup":[{"type":"flex","duration":1000}],"programs":{}}"""));
        _app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Tunarr:BaseUrl", _tunarr.Url + "/api/");
            b.UseSetting("Tools:Mode", "write");
            b.UseSetting("Data:Path", Path.Combine(file, "data"));
        });
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_app.Server.BaseAddress, "/mcp") },
            _app.CreateClient(), ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);

        var result = await client.CallToolAsync("preview_schedule", new Dictionary<string, object?>
        {
            ["channel"] = "99",
            ["schedule"] = JsonSerializer.Deserialize<JsonElement>($$"""{"type":"time","slots":[{{Bluey}}]}"""),
        }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task Channels_outside_the_allowlist_are_read_only()
    {
        StubChannel(scheduled: true);
        await using var client = await ConnectAsync(writableChannels: "7");

        var result = await Save(client, $$"""{"type":"time","slots":[{{Bluey}}]}""");

        Assert.True(result.IsError);
        Assert.Contains("read-only here", Text(result), StringComparison.Ordinal);
        Assert.Empty(Writes());
    }
}
