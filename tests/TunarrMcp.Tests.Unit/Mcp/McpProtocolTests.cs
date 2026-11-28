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
