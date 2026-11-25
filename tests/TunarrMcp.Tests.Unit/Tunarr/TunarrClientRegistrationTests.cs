using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TunarrMcp.Tunarr;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TunarrMcp.Tests.Unit.Tunarr;

/// <summary>The client as the app wires it: base URL from config, optional basic auth, readiness endpoint.</summary>
public sealed class TunarrClientRegistrationTests(TestAppFactory factory) : IClassFixture<TestAppFactory>, IDisposable
{
    private readonly WireMockServer _tunarr = WireMockServer.Start();

    public void Dispose() => _tunarr.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> App(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(b =>
        {
            // No trailing slash on purpose: the app must add it, or "/api" would be dropped.
            b.UseSetting("Tunarr:BaseUrl", _tunarr.Url + "/api");
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);
        });

    private void StubHealth(int status = 200) =>
        _tunarr.Given(Request.Create().WithPath("/api/system/health").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody("""{"FfmpegVersion":{"type":"healthy"}}"""));

    [Fact]
    public async Task Basic_auth_header_is_sent_when_configured()
    {
        StubHealth();
        await using var app = App(("Tunarr:BasicAuthUser", "tunarr"), ("Tunarr:BasicAuthPassword", "s3cret"));

        await app.Services.GetRequiredService<TunarrClient>().GetHealthAsync(Ct);

        var header = Assert.Single(_tunarr.LogEntries).RequestMessage!.Headers!["Authorization"];
        Assert.Equal("Basic dHVuYXJyOnMzY3JldA==", Assert.Single(header)); // tunarr:s3cret
    }

    [Fact]
    public async Task No_auth_header_without_basic_auth_config()
    {
        StubHealth();
        await using var app = App();

        await app.Services.GetRequiredService<TunarrClient>().GetHealthAsync(Ct);

        Assert.False(Assert.Single(_tunarr.LogEntries).RequestMessage!.Headers!.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Ready_is_healthy_when_tunarr_answers()
    {
        StubHealth();
        await using var app = App();

        using var response = await app.CreateClient().GetAsync(new Uri("/healthz/ready", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_is_unhealthy_when_tunarr_errors()
    {
        // 404 is not retried, so this stays fast. 5xx and connection failures are retried until the 5 s check timeout.
        StubHealth(404);
        await using var app = App();

        using var response = await app.CreateClient().GetAsync(new Uri("/healthz/ready", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Liveness_does_not_call_tunarr()
    {
        await using var app = App();

        using var response = await app.CreateClient().GetAsync(new Uri("/healthz", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_tunarr.LogEntries);
    }
}
