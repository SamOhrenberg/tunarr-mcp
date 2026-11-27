using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using TunarrMcp.Server.Auth.Registration;

namespace TunarrMcp.Tests.Unit.Auth.Registration;

/// <summary>DCR and CIMD through the real host. Success is proven by OpenIddict accepting the client at /connect/authorize.</summary>
public sealed class ClientRegistrationTests(TestAppFactory factory) : IClassFixture<TestAppFactory>, IAsyncLifetime
{
    private const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";
    private const string DocumentUrl = "https://client.example/oauth/metadata.json";

    private readonly string _data = Directory.CreateTempSubdirectory("tunarr-mcp-reg-").FullName;
    private readonly FakeFetcher _documents = new();
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _http = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _app = factory.WithEmbeddedAuth(_data)
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IClientMetadataFetcher>(_documents)));
        _http = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_data, recursive: true);
    }

    // ---- metadata ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Metadata_advertises_both_registration_methods()
    {
        var metadata = await _http.GetFromJsonAsync<JsonElement>(new Uri("/.well-known/oauth-authorization-server", UriKind.Relative), Ct);

        Assert.Equal("http://localhost/register", metadata.GetProperty("registration_endpoint").GetString());
        Assert.True(metadata.GetProperty("client_id_metadata_document_supported").GetBoolean());
        Assert.Contains("none", metadata.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(e => e.GetString()));
    }

    // ---- DCR -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dcr_registers_a_public_client_that_can_start_authorization()
    {
        using var response = await RegisterAsync(new
        {
            redirect_uris = new[] { ClaudeCallback },
            client_name = "Claude",
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var clientId = body.GetProperty("client_id").GetString()!;
        Assert.StartsWith("dcr_", clientId, StringComparison.Ordinal);
        Assert.Equal("none", body.GetProperty("token_endpoint_auth_method").GetString());
        Assert.False(body.TryGetProperty("client_secret", out _));

        await AssertAuthorizationStartsAsync(clientId, ClaudeCallback);
    }

    [Fact]
    public async Task Dcr_accepts_claude_code_loopback_on_any_port()
    {
        using var response = await RegisterAsync(new { redirect_uris = new[] { "http://localhost:53682/callback" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"redirect_uris":["https://evil.example/callback"]}""", "invalid_redirect_uri")]
    [InlineData("""{"redirect_uris":[]}""", "invalid_redirect_uri")]
    [InlineData("""{"client_name":"no redirects"}""", "invalid_redirect_uri")]
    [InlineData("""{"redirect_uris":["https://claude.ai/api/mcp/auth_callback"],"token_endpoint_auth_method":"client_secret_basic"}""", "invalid_client_metadata")]
    [InlineData("""{"redirect_uris":["https://claude.ai/api/mcp/auth_callback"],"grant_types":["client_credentials"]}""", "invalid_client_metadata")]
    [InlineData("""not json""", "invalid_client_metadata")]
    public async Task Dcr_rejects_what_this_server_does_not_support(string json, string expectedError)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(new Uri("/register", UriKind.Relative), content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedError, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    // ---- CIMD ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cimd_client_is_registered_from_its_document_on_first_authorization()
    {
        _documents[DocumentUrl] = $$"""{"client_id":"{{DocumentUrl}}","client_name":"Claude","redirect_uris":["{{ClaudeCallback}}"],"token_endpoint_auth_method":"none"}""";

        await AssertAuthorizationStartsAsync(DocumentUrl, ClaudeCallback);

        await using var scope = _app.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = await applications.FindByClientIdAsync(DocumentUrl, Ct);
        Assert.NotNull(app);
        Assert.Equal("Claude", await applications.GetDisplayNameAsync(app, Ct));
    }

    [Fact]
    public async Task Cimd_document_changes_are_picked_up_on_the_next_authorization()
    {
        _documents[DocumentUrl] = $$"""{"client_id":"{{DocumentUrl}}","client_name":"Old","redirect_uris":["{{ClaudeCallback}}"]}""";
        await AssertAuthorizationStartsAsync(DocumentUrl, ClaudeCallback);

        _documents[DocumentUrl] = $$"""{"client_id":"{{DocumentUrl}}","client_name":"New","redirect_uris":["http://localhost/callback"]}""";
        await AssertAuthorizationStartsAsync(DocumentUrl, "http://localhost:53682/callback");

        using var stale = await _http.GetAsync(AuthorizeUrl(DocumentUrl, ClaudeCallback), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode); // the old redirect is no longer registered
    }

    [Fact]
    public async Task Cimd_fetch_failure_is_shown_in_the_browser_not_redirected()
    {
        using var response = await _http.GetAsync(AuthorizeUrl("https://client.example/missing.json", ClaudeCallback), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("invalid_client", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Cimd_document_with_only_disallowed_redirects_is_rejected()
    {
        _documents[DocumentUrl] = $$"""{"client_id":"{{DocumentUrl}}","redirect_uris":["https://evil.example/cb"]}""";

        using var response = await _http.GetAsync(AuthorizeUrl(DocumentUrl, "https://evil.example/cb"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("redirect_uris", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> RegisterAsync(object metadata) =>
        await _http.PostAsJsonAsync(new Uri("/register", UriKind.Relative), metadata, Ct);

    /// <summary>OpenIddict accepted the client and redirect URI if it sends the browser on to /login.</summary>
    private async Task AssertAuthorizationStartsAsync(string clientId, string redirectUri)
    {
        using var response = await _http.GetAsync(AuthorizeUrl(clientId, redirectUri), Ct);

        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal("/login", response.Headers.Location!.AbsolutePath);
    }

    private static Uri AuthorizeUrl(string clientId, string redirectUri) => new(QueryHelpers.AddQueryString("/connect/authorize",
        new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            ["code_challenge_method"] = "S256",
            ["resource"] = "http://localhost/mcp",
        }), UriKind.Relative);

    private sealed class FakeFetcher : Dictionary<string, string>, IClientMetadataFetcher
    {
        public Task<string> FetchAsync(Uri url, CancellationToken cancellationToken) =>
            TryGetValue(url.OriginalString, out var json)
                ? Task.FromResult(json)
                : throw new ClientMetadataException("Fetching the client metadata document returned 404.");
    }
}
