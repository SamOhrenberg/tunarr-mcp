using Microsoft.Extensions.Options;
using TunarrMcp.Server.Auth.Registration;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Tests.Unit.Auth.Registration;

public sealed class ClientMetadataDocumentTests
{
    private static readonly Uri Url = new("https://client.example/oauth/metadata.json");
    private static readonly RedirectUriPolicy Policy = new(Options.Create(new AuthOptions()));

    private static string Doc(string extra = "", string redirects = """["https://claude.ai/api/mcp/auth_callback"]""") =>
        $$"""{"client_id":"{{Url}}","client_name":"Claude","redirect_uris":{{redirects}}{{extra}}}""";

    [Fact]
    public void Valid_document_parses()
    {
        var doc = ClientMetadataDocument.Parse(Url, Doc(""","token_endpoint_auth_method":"none","grant_types":["authorization_code"]"""));

        Assert.Equal("Claude", doc.ClientName);
        Assert.Equal(new Uri("https://claude.ai/api/mcp/auth_callback"), Assert.Single(doc.RedirectUris));
    }

    [Theory]
    [InlineData("""{"client_id":"https://other.example/meta","redirect_uris":["https://claude.ai/api/mcp/auth_callback"]}""", "does not match")]
    [InlineData("""{"client_id":"https://client.example/oauth/metadata.json"}""", "redirect_uris")]
    [InlineData("""{"client_id":"https://client.example/oauth/metadata.json","redirect_uris":[]}""", "redirect_uris")]
    [InlineData("""{"client_id":"https://client.example/oauth/metadata.json","redirect_uris":["x"],"client_secret":"s"}""", "client_secret")]
    [InlineData("""{"client_id":"https://client.example/oauth/metadata.json","redirect_uris":["x"],"token_endpoint_auth_method":"private_key_jwt"}""", "public clients")]
    [InlineData("""["not an object"]""", "JSON object")]
    [InlineData("""{not json""", "not valid JSON")]
    public void Invalid_documents_are_rejected(string json, string expected)
    {
        var e = Assert.Throws<ClientMetadataException>(() => ClientMetadataDocument.Parse(Url, json));

        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://client.example/meta")]
    [InlineData("https://client.example/")]
    [InlineData("https://client.example/a/../meta")]
    [InlineData("https://user@client.example/meta")]
    [InlineData("https://client.example/meta#frag")]
    public void Client_id_url_must_be_well_formed(string clientId)
    {
        Assert.Throws<ClientMetadataException>(() => ClientMetadataDocument.ValidateUrl(clientId));
    }

    [Fact]
    public void Disallowed_redirects_are_dropped_and_none_left_is_an_error()
    {
        var mixed = ClientMetadataDocument.Parse(Url, Doc(redirects: """["https://evil.example/cb","https://claude.ai/api/mcp/auth_callback"]"""));
        Assert.Equal([new Uri("https://claude.ai/api/mcp/auth_callback")], mixed.AllowedRedirectUris(Policy, requested: null));

        var evil = ClientMetadataDocument.Parse(Url, Doc(redirects: """["https://evil.example/cb"]"""));
        Assert.Throws<ClientMetadataException>(() => evil.AllowedRedirectUris(Policy, requested: null));
    }

    [Fact]
    public void Requested_loopback_port_is_added_when_the_document_lists_the_portless_uri()
    {
        var doc = ClientMetadataDocument.Parse(Url, Doc(redirects: """["http://localhost/callback"]"""));

        var allowed = doc.AllowedRedirectUris(Policy, new Uri("http://localhost:53682/callback"));

        Assert.Contains(new Uri("http://localhost:53682/callback"), allowed);
        Assert.DoesNotContain(new Uri("http://localhost:53682/elsewhere"), doc.AllowedRedirectUris(Policy, new Uri("http://localhost:53682/elsewhere")));
    }
}
