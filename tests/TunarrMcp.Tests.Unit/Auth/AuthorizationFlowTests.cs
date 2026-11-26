using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OtpNet;
using TunarrMcp.Server.Auth;

namespace TunarrMcp.Tests.Unit.Auth;

/// <summary>
/// The whole browser + client flow against the in-process host, the way Claude drives it:
/// authorize → login → consent → code → token → refresh.
/// </summary>
public sealed partial class AuthorizationFlowTests(TestAppFactory factory) : IClassFixture<TestAppFactory>, IAsyncLifetime
{
    private const string ClientId = "test-client";
    private const string RedirectUri = "http://localhost/callback";
    private const string Resource = "http://localhost/mcp";

    private readonly string _data = Directory.CreateTempSubdirectory("tunarr-mcp-flow-").FullName;
    private readonly string _verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _browser = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _app = factory.WithEmbeddedAuth(_data);
        _browser = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        await using var scope = _app.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await applications.CreateAsync(
            McpClientDescriptor.Create(ClientId, "Test client", [new Uri(RedirectUri)], new Uri(Resource)), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _browser.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_data, recursive: true);
    }

    // ---- the happy path ------------------------------------------------------------------------------------

    [Fact]
    public async Task Full_flow_issues_access_and_refresh_tokens()
    {
        var consent = await SignInAndOpenConsentAsync(AuthorizeUrl());
        Assert.Contains("Test client", consent, StringComparison.Ordinal);
        Assert.Contains("<strong>localhost</strong>", consent, StringComparison.Ordinal);

        var callback = await DecideAsync(consent, "approve");
        Assert.StartsWith(RedirectUri + "?", callback.AbsoluteUri, StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal("xyz", query["state"]);
        Assert.Equal("http://localhost/", query["iss"]); // RFC 9207

        var tokens = await TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = query["code"]!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = _verifier,
            ["resource"] = Resource,
        });
        Assert.Equal("Bearer", tokens.GetProperty("token_type").GetString());
        Assert.InRange(tokens.GetProperty("expires_in").GetInt32(), 890, 900);
        Assert.Contains("offline_access", tokens.GetProperty("scope").GetString(), StringComparison.Ordinal);
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var refreshed = await TokenAsync(new()
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
        using var response = await _browser.GetAsync(new Uri("/login", UriKind.Relative), Ct);

        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    // ---- refusals ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deny_returns_access_denied_to_the_client()
    {
        var consent = await SignInAndOpenConsentAsync(AuthorizeUrl());

        var callback = await DecideAsync(consent, "deny");

        Assert.Equal("access_denied", QueryHelpers.ParseQuery(callback.Query)["error"]);
    }

    [Fact]
    public async Task Unregistered_redirect_uri_is_rejected_without_redirecting()
    {
        using var response = await _browser.GetAsync(AuthorizeUrl(redirectUri: "https://evil.example/callback"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Missing_pkce_is_rejected()
    {
        using var response = await _browser.GetAsync(AuthorizeUrl(withPkce: false), Ct);

        // OpenIddict answers this one directly (400) rather than redirecting the error to the client.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("code_challenge", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompt_none_is_refused_because_consent_is_always_asked()
    {
        var consent = await SignInAndOpenConsentAsync(AuthorizeUrl()); // signed in, so only consent is missing
        Assert.NotEmpty(consent);

        using var response = await _browser.GetAsync(AuthorizeUrl(extra: "&prompt=none"), Ct);

        Assert.Contains("error=consent_required", response.Headers.Location?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consent_post_without_csrf_token_is_rejected()
    {
        var consent = await SignInAndOpenConsentAsync(AuthorizeUrl());
        var fields = HiddenFields(consent).Where(f => f.Key != "__RequestVerificationToken").Append(new("decision", "approve"));

        using var response = await _browser.PostAsync(new Uri("/connect/authorize", UriKind.Relative), new FormUrlEncodedContent(fields), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_never_redirects_off_site()
    {
        var login = await GetStringAsync(new Uri("/login", UriKind.Relative));
        var fields = HiddenFields(login).Where(f => f.Key != "returnUrl").Concat(Credentials()).Append(new("returnUrl", "https://evil.example/"));

        using var response = await _browser.PostAsync(new Uri("/login", UriKind.Relative), new FormUrlEncodedContent(fields), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/connect/authorize", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Login_attempts_are_rate_limited_per_ip()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var response = await _browser.PostAsync(new Uri("/login", UriKind.Relative), new FormUrlEncodedContent([]), Ct);
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses.Take(5), s => Assert.Equal(HttpStatusCode.BadRequest, s)); // no CSRF token
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[5]);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private Uri AuthorizeUrl(string redirectUri = RedirectUri, bool withPkce = true, string extra = "")
    {
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier)));
        var url = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "tunarr.read tunarr.write",
            ["state"] = "xyz",
            ["resource"] = Resource,
            ["code_challenge"] = withPkce ? challenge : null,
            ["code_challenge_method"] = withPkce ? "S256" : null,
        });
        return new Uri(url + extra, UriKind.Relative);
    }

    /// <summary>Follows authorize → login redirect, signs in, follows back, and returns the consent page HTML.</summary>
    private async Task<string> SignInAndOpenConsentAsync(Uri authorizeUrl)
    {
        using var toLogin = await _browser.GetAsync(authorizeUrl, Ct);
        Assert.Equal(HttpStatusCode.Redirect, toLogin.StatusCode);
        Assert.Equal("/login", toLogin.Headers.Location!.AbsolutePath);

        var login = await GetStringAsync(toLogin.Headers.Location);
        using var signedIn = await _browser.PostAsync(new Uri("/login", UriKind.Relative),
            new FormUrlEncodedContent(HiddenFields(login).Concat(Credentials())), Ct);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

        return await GetStringAsync(signedIn.Headers.Location!);
    }

    private async Task<Uri> DecideAsync(string consentHtml, string decision)
    {
        using var response = await _browser.PostAsync(new Uri("/connect/authorize", UriKind.Relative),
            new FormUrlEncodedContent(HiddenFields(consentHtml).Append(new("decision", decision))), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!;
    }

    private async Task<JsonElement> TokenAsync(Dictionary<string, string> form)
    {
        using var client = _app.CreateClient(); // the MCP client's back channel, no browser cookies
        using var response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(form), Ct);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(response.IsSuccessStatusCode, json.ToString());
        return json;
    }

    private async Task<string> GetStringAsync(Uri url)
    {
        using var response = await _browser.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static KeyValuePair<string, string>[] Credentials() =>
    [
        new("username", TestAdmin.User),
        new("password", TestAdmin.Password),
        new("code", new Totp(Base32Encoding.ToBytes(TestAdmin.TotpSecret)).ComputeTotp(DateTime.UtcNow)),
    ];

    private static IEnumerable<KeyValuePair<string, string>> HiddenFields(string html) =>
        HiddenInput().Matches(html).Select(m => KeyValuePair.Create(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)));

    [GeneratedRegex("""<input type=hidden name="?([^" >]+)"? value="([^"]*)">""")]
    private static partial Regex HiddenInput();
}
