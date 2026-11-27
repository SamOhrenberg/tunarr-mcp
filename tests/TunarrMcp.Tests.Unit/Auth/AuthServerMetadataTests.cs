using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TunarrMcp.Tests.Unit.Auth;

/// <summary>
/// The authorization server metadata (RFC 8414) is the first thing an MCP client reads after the protected-resource
/// metadata, and the most common place for a connector to break. These pin the fields Claude depends on.
/// </summary>
public sealed class AuthServerMetadataTests(TestAppFactory factory) : IClassFixture<TestAppFactory>, IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("tunarr-mcp-auth-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // SQLite keeps pooled connections open; release them so the directory can be deleted.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_data, recursive: true);
    }

    private static async Task<JsonElement> GetJsonAsync(WebApplicationFactory<Program> app, string path)
    {
        using var client = app.CreateClient();
        return await client.GetFromJsonAsync<JsonElement>(new Uri(path, UriKind.Relative), Ct);
    }

    private static string[] Strings(JsonElement metadata, string property) =>
        metadata.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public async Task Metadata_is_served_at_the_rfc8414_location()
    {
        await using var app = factory.WithEmbeddedAuth(_data);

        var metadata = await GetJsonAsync(app, "/.well-known/oauth-authorization-server");

        Assert.Equal("http://localhost/", metadata.GetProperty("issuer").GetString());
        Assert.Equal("http://localhost/connect/authorize", metadata.GetProperty("authorization_endpoint").GetString());
        Assert.Equal("http://localhost/connect/token", metadata.GetProperty("token_endpoint").GetString());
        Assert.Equal("http://localhost/.well-known/jwks", metadata.GetProperty("jwks_uri").GetString());
    }

    [Fact]
    public async Task Metadata_advertises_only_s256_pkce_and_the_code_flow()
    {
        await using var app = factory.WithEmbeddedAuth(_data);

        var metadata = await GetJsonAsync(app, "/.well-known/oauth-authorization-server");

        Assert.Equal(["S256"], Strings(metadata, "code_challenge_methods_supported"));
        Assert.Equal(["code"], Strings(metadata, "response_types_supported"));
        Assert.Equivalent(new[] { "authorization_code", "refresh_token" }, Strings(metadata, "grant_types_supported"));
        Assert.Equivalent(new[] { "tunarr.read", "tunarr.write", "offline_access" }, Strings(metadata, "scopes_supported"));
    }

    [Fact]
    public async Task Openid_configuration_is_served_as_a_fallback()
    {
        await using var app = factory.WithEmbeddedAuth(_data);

        var metadata = await GetJsonAsync(app, "/.well-known/openid-configuration");

        Assert.Equal("http://localhost/", metadata.GetProperty("issuer").GetString());
    }

    [Fact]
    public async Task Keys_and_database_persist_across_restarts()
    {
        string kid;
        await using (var first = factory.WithEmbeddedAuth(_data))
            kid = (await GetJsonAsync(first, "/.well-known/jwks")).GetProperty("keys")[0].GetProperty("kid").GetString()!;

        await using var second = factory.WithEmbeddedAuth(_data);
        var jwks = await GetJsonAsync(second, "/.well-known/jwks");

        Assert.Equal(kid, jwks.GetProperty("keys")[0].GetProperty("kid").GetString());
        Assert.True(File.Exists(Path.Combine(_data, "keys", "signing.pem")));
        Assert.True(File.Exists(Path.Combine(_data, "keys", "encryption.pem")));
        Assert.True(File.Exists(Path.Combine(_data, "auth.db")));
    }

    [Fact]
    public async Task Data_protection_keys_are_kept_on_the_data_volume()
    {
        await using var app = factory.WithEmbeddedAuth(_data);
        using var client = app.CreateClient();

        using var login = await client.GetAsync(new Uri("/login", UriKind.Relative), Ct); // issues a CSRF token

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_data, "dataprotection"), "key-*.xml"));
    }

    [Fact]
    public async Task No_auth_server_endpoints_when_auth_is_off()
    {
        using var client = factory.CreateClient(); // Auth:Mode=None

        using var response = await client.GetAsync(new Uri("/.well-known/oauth-authorization-server", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
