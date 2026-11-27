using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using static TunarrMcp.Tests.Unit.Auth.OAuthTestClient;

namespace TunarrMcp.Tests.Unit.Auth;

/// <summary>
/// The whole browser + client flow against the in-process host, the way Claude drives it:
/// authorize → login → consent → code → token → refresh.
/// </summary>
public sealed class AuthorizationFlowTests(TestAppFactory factory) : IClassFixture<TestAppFactory>, IAsyncLifetime
{
    private readonly string _data = Directory.CreateTempSubdirectory("tunarr-mcp-flow-").FullName;
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

    // ---- the happy path ------------------------------------------------------------------------------------

    [Fact]
    public async Task Full_flow_issues_access_and_refresh_tokens()
    {
        var consent = await _oauth.SignInAndOpenConsentAsync(_oauth.AuthorizeUrl());
        Assert.Contains("Test client", consent, StringComparison.Ordinal);
        Assert.Contains("<strong>localhost</strong>", consent, StringComparison.Ordinal);

        var callback = await _oauth.DecideAsync(consent, "approve");
        Assert.StartsWith(RedirectUri + "?", callback.AbsoluteUri, StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal("xyz", query["state"]);
        Assert.Equal("http://localhost/", query["iss"]); // RFC 9207

        var tokens = await _oauth.TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = query["code"]!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = _oauth.Verifier,
            ["resource"] = Resource,
        });
        Assert.Equal("Bearer", tokens.GetProperty("token_type").GetString());
        Assert.InRange(tokens.GetProperty("expires_in").GetInt32(), 890, 900);
        Assert.Contains("offline_access", tokens.GetProperty("scope").GetString(), StringComparison.Ordinal);
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var refreshed = await _oauth.TokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        });
        Assert.NotEqual(refreshToken, refreshed.GetProperty("refresh_token").GetString()); // rotated
        Assert.False(string.IsNullOrEmpty(refreshed.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task Pages_cannot_be_framed_or_cached()
    {
        using var response = await _oauth.Browser.GetAsync(new Uri("/login", UriKind.Relative), Ct);

        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    // ---- refusals ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deny_returns_access_denied_to_the_client()
    {
        var consent = await _oauth.SignInAndOpenConsentAsync(_oauth.AuthorizeUrl());

        var callback = await _oauth.DecideAsync(consent, "deny");

        Assert.Equal("access_denied", QueryHelpers.ParseQuery(callback.Query)["error"]);
    }

    [Fact]
    public async Task Unregistered_redirect_uri_is_rejected_without_redirecting()
    {
        using var response = await _oauth.Browser.GetAsync(_oauth.AuthorizeUrl(redirectUri: "https://evil.example/callback"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Missing_pkce_is_rejected()
    {
        using var response = await _oauth.Browser.GetAsync(_oauth.AuthorizeUrl(withPkce: false), Ct);

        // OpenIddict answers this one directly (400) rather than redirecting the error to the client.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("code_challenge", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompt_none_is_refused_because_consent_is_always_asked()
    {
        var consent = await _oauth.SignInAndOpenConsentAsync(_oauth.AuthorizeUrl()); // signed in, so only consent is missing
        Assert.NotEmpty(consent);

        using var response = await _oauth.Browser.GetAsync(_oauth.AuthorizeUrl(extra: "&prompt=none"), Ct);

        Assert.Contains("error=consent_required", response.Headers.Location?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consent_post_without_csrf_token_is_rejected()
    {
        var consent = await _oauth.SignInAndOpenConsentAsync(_oauth.AuthorizeUrl());
        var fields = HiddenFields(consent).Where(f => f.Key != "__RequestVerificationToken").Append(new("decision", "approve"));

        using var response = await _oauth.Browser.PostAsync(new Uri("/connect/authorize", UriKind.Relative), new FormUrlEncodedContent(fields), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_never_redirects_off_site()
    {
        var login = await _oauth.GetStringAsync(new Uri("/login", UriKind.Relative));
        var fields = HiddenFields(login).Where(f => f.Key != "returnUrl").Concat(Credentials()).Append(new("returnUrl", "https://evil.example/"));

        using var response = await _oauth.Browser.PostAsync(new Uri("/login", UriKind.Relative), new FormUrlEncodedContent(fields), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/connect/authorize", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Login_attempts_are_rate_limited_per_ip()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var response = await _oauth.Browser.PostAsync(new Uri("/login", UriKind.Relative), new FormUrlEncodedContent([]), Ct);
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses.Take(5), s => Assert.Equal(HttpStatusCode.BadRequest, s)); // no CSRF token
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[5]);
    }
}
