using System.Net;
using Microsoft.Extensions.Options;
using TunarrMcp.Server.Auth.Registration;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Tests.Unit.Auth.Registration;

public sealed class RedirectUriPolicyTests
{
    private static readonly RedirectUriPolicy Default = new(Options.Create(new AuthOptions()));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("http://localhost/callback")]
    [InlineData("http://localhost:53682/callback")]  // Claude Code picks a free port
    [InlineData("http://127.0.0.1:8123/callback")]
    public void Claude_callbacks_are_allowed_by_default(string uri)
    {
        Assert.True(Default.IsAllowed(new Uri(uri)));
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback/extra")]  // different path
    [InlineData("http://claude.ai/api/mcp/auth_callback")]          // downgraded scheme
    [InlineData("https://claude.ai.evil.example/api/mcp/auth_callback")]
    [InlineData("https://claude.ai@evil.example/api/mcp/auth_callback")] // userinfo trick: host is evil.example
    [InlineData("https://claude.ai/api/mcp/auth_callback#x")]
    [InlineData("https://claude.ai/api/mcp/auth_callback?next=x")]
    [InlineData("http://localhost:53682/other")]
    [InlineData("https://localhost/callback")]   // loopback rule is for http only
    [InlineData("http://192.168.1.50/callback")] // LAN is not loopback
    public void Anything_else_is_refused(string uri)
    {
        Assert.False(Default.IsAllowed(new Uri(uri)));
    }

    [Fact]
    public void Configured_list_replaces_the_defaults()
    {
        var policy = new RedirectUriPolicy(Options.Create(new AuthOptions { AllowedRedirectUris = "https://app.example/cb, http://localhost/oauth" }));

        Assert.True(policy.IsAllowed(new Uri("https://app.example/cb")));
        Assert.True(policy.IsAllowed(new Uri("http://localhost:4000/oauth")));
        Assert.False(policy.IsAllowed(new Uri("https://claude.ai/api/mcp/auth_callback")));
    }
}

public sealed class PublicAddressTests
{
    [Theory]
    [InlineData("160.79.104.10", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.20.0.3", false)]      // Docker bridge networks
    [InlineData("192.168.1.50", false)]    // home LAN, e.g. where Tunarr runs
    [InlineData("169.254.169.254", false)] // cloud metadata endpoints
    [InlineData("100.100.1.1", false)]     // CGNAT / Tailscale
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:192.168.1.50", false)] // IPv4-mapped private address
    public void Only_public_addresses_may_be_fetched(string address, bool expected)
    {
        Assert.Equal(expected, ClientMetadataFetcher.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("https://localhost/metadata.json")]
    [InlineData("https://127.0.0.1:8443/metadata.json")]
    [InlineData("https://192.168.1.50:8000/metadata.json")]
    public async Task Real_fetcher_refuses_to_connect_to_private_hosts(string url)
    {
        using var http = new HttpClient(ClientMetadataFetcher.CreateHandler());
        var fetcher = new ClientMetadataFetcher(http);

        var e = await Assert.ThrowsAsync<ClientMetadataException>(() => fetcher.FetchAsync(new Uri(url), TestContext.Current.CancellationToken));

        Assert.Contains("does not resolve to a public address", e.Message, StringComparison.Ordinal);
    }
}
