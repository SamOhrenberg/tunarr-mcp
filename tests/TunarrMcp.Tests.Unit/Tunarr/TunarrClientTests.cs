using System.Net;
using TunarrMcp.Tunarr;
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
