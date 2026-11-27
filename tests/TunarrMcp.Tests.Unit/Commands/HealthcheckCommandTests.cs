using TunarrMcp.Server.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TunarrMcp.Tests.Unit.Commands;

public sealed class HealthcheckCommandTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Dispose();

    [Theory]
    [InlineData(200, 0)]
    [InlineData(503, 1)]
    public async Task Exit_code_follows_the_liveness_endpoint(int status, int expected)
    {
        _server.Given(Request.Create().WithPath("/healthz")).RespondWith(Response.Create().WithStatusCode(status));

        Assert.Equal(expected, await HealthcheckCommand.RunAsync([_server.Url + "/healthz"]));
    }

    [Fact]
    public async Task Unreachable_server_is_unhealthy()
    {
        var url = _server.Url + "/healthz";
        _server.Stop();

        Assert.Equal(1, await HealthcheckCommand.RunAsync([url]));
    }
}
