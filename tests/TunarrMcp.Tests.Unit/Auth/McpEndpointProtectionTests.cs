using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using OpenIddict.Abstractions;
using OpenIddict.Validation;

namespace TunarrMcp.Tests.Unit.Auth;

/// <summary>/mcp in Embedded mode: the 401 discovery handshake, token validation, rate limit and request guard.</summary>
public sealed class McpEndpointProtectionTests(TestAppFactory factory) : IClassFixture<TestAppFactory>, IAsyncLifetime
{
    private readonly string _data = Directory.CreateTempSubdirectory("tunarr-mcp-protect-").FullName;
    private WebApplicationFactory<Program> _app = null!;
    private OAuthTestClient _oauth = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _app = factory.WithEmbeddedAuth(_data);
        _oauth = new OAuthTestClient(_app);
        await _oauth.RegisterClientAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _oauth.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_data, recursive: true);
    }

    // ---- discovery: what Claude sees before it has a token -------------------------------------------------

    [Fact]
    public async Task Anonymous_call_gets_401_pointing_at_protected_resource_metadata()
    {
        using var response = await PostToolsListAsync(token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"", challenge.Parameter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protected_resource_metadata_names_the_resource_and_issuer()
    {
        using var client = _app.CreateClient();

        var metadata = await client.GetFromJsonAsync<JsonElement>(new Uri("/.well-known/oauth-protected-resource/mcp", UriKind.Relative), Ct);

        Assert.Equal("http://localhost/mcp", metadata.GetProperty("resource").GetString()); // must equal the URL Claude connects to
        Assert.Equal("http://localhost/", metadata.GetProperty("authorization_servers")[0].GetString());
        Assert.Contains("offline_access", metadata.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Tokens_must_be_issued_for_the_mcp_endpoint()
    {
        var validation = _app.Services.GetRequiredService<IOptions<OpenIddictValidationOptions>>().Value;

        Assert.Equal(["http://localhost/mcp"], validation.Audiences);
    }

    // ---- with a token --------------------------------------------------------------------------------------

    [Fact]
    public async Task Token_from_the_oauth_flow_opens_the_mcp_endpoint()
    {
        var token = (await _oauth.GetTokensAsync()).GetProperty("access_token").GetString()!;
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(_app.Server.BaseAddress, "/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            },
            _app.CreateClient(), ownsHttpClient: true);
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: Ct);

        var tools = await mcp.ListToolsAsync(cancellationToken: Ct);

        Assert.Contains(tools, t => t.Name == "system_status");
    }

    [Fact]
    public async Task Garbage_token_is_refused()
    {
        using var response = await PostToolsListAsync("not-a-real-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_token_is_refused_immediately()
    {
        var token = (await _oauth.GetTokensAsync()).GetProperty("access_token").GetString()!;
        using (var before = await PostToolsListAsync(token))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            await foreach (var entry in tokens.ListAsync(count: null, offset: null, Ct))
                await tokens.TryRevokeAsync(entry, Ct);
        }

        using var after = await PostToolsListAsync(token);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Each_token_is_limited_to_60_requests_a_minute()
    {
        var token = (await _oauth.GetTokensAsync()).GetProperty("access_token").GetString()!;

        for (var i = 0; i < 60; i++)
        {
            using var ok = await PostToolsListAsync(token);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await PostToolsListAsync(token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    // ---- request guard (runs before auth, in every mode) ---------------------------------------------------

    [Fact]
    public async Task Foreign_origin_is_rejected()
    {
        using var foreign = await PostToolsListAsync(token: null, origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        using var own = await PostToolsListAsync(token: null, origin: "http://localhost");
        Assert.Equal(HttpStatusCode.Unauthorized, own.StatusCode); // got past the guard, stopped by auth
    }

    [Theory]
    [InlineData("203.0.113.9", HttpStatusCode.OK)]
    [InlineData("198.51.100.1", HttpStatusCode.Forbidden)]
    public async Task Ip_allowlist_applies_when_configured(string remoteIp, HttpStatusCode expected)
    {
        // Auth:Mode=None so the guard is the only gate.
        await using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:AllowedIpRanges", "203.0.113.0/24");
            b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(new RemoteIpFilter(IPAddress.Parse(remoteIp))));
        });
        using var client = app.CreateClient();

        using var response = await client.SendAsync(ToolsListRequest(token: null, origin: null), Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> PostToolsListAsync(string? token, string? origin = null)
    {
        using var client = _app.CreateClient();
        return await client.SendAsync(ToolsListRequest(token, origin), Ct);
    }

    private static HttpRequestMessage ToolsListRequest(string? token, string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return request;
    }

    private sealed class RemoteIpFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
