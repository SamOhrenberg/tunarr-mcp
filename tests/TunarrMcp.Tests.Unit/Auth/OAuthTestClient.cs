using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OtpNet;
using TunarrMcp.Server.Auth;

namespace TunarrMcp.Tests.Unit.Auth;

/// <summary>
/// Drives the OAuth flow against an in-process host the way Claude and a browser do:
/// authorize → login → consent → code → token. Holds the browser's cookies and the PKCE verifier.
/// </summary>
internal sealed partial class OAuthTestClient : IDisposable
{
    public const string ClientId = "test-client";
    public const string RedirectUri = "http://localhost/callback";
    public const string Resource = "http://localhost/mcp";

    private readonly WebApplicationFactory<Program> _app;

    public OAuthTestClient(WebApplicationFactory<Program> app)
    {
        _app = app;
        Browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    public HttpClient Browser { get; }

    public string Verifier { get; } = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Browser.Dispose();

    public async Task RegisterClientAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await applications.CreateAsync(McpClientDescriptor.Create(ClientId, "Test client", [new Uri(RedirectUri)], new Uri(Resource)), Ct);
    }

    /// <summary>The whole flow; returns the token response.</summary>
    public async Task<JsonElement> GetTokensAsync()
    {
        var consent = await SignInAndOpenConsentAsync(AuthorizeUrl());
        var callback = await DecideAsync(consent, "approve");
        return await TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = QueryHelpers.ParseQuery(callback.Query)["code"]!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = Verifier,
            ["resource"] = Resource,
        });
    }

    public Uri AuthorizeUrl(string redirectUri = RedirectUri, bool withPkce = true, string extra = "")
    {
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
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
    public async Task<string> SignInAndOpenConsentAsync(Uri authorizeUrl)
    {
        using var toLogin = await Browser.GetAsync(authorizeUrl, Ct);
        Assert.Equal(HttpStatusCode.Redirect, toLogin.StatusCode);
        Assert.Equal("/login", toLogin.Headers.Location!.AbsolutePath);

        var login = await GetStringAsync(toLogin.Headers.Location);
        using var signedIn = await Browser.PostAsync(new Uri("/login", UriKind.Relative),
            new FormUrlEncodedContent(HiddenFields(login).Concat(Credentials())), Ct);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

        return await GetStringAsync(signedIn.Headers.Location!);
    }

    public async Task<Uri> DecideAsync(string consentHtml, string decision)
    {
        using var response = await Browser.PostAsync(new Uri("/connect/authorize", UriKind.Relative),
            new FormUrlEncodedContent(HiddenFields(consentHtml).Append(new("decision", decision))), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!;
    }

    public async Task<JsonElement> TokenAsync(Dictionary<string, string> form)
    {
        using var client = _app.CreateClient(); // the MCP client's back channel, no browser cookies
        using var response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(form), Ct);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(response.IsSuccessStatusCode, json.ToString());
        return json;
    }

    public async Task<string> GetStringAsync(Uri url)
    {
        using var response = await Browser.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    public static KeyValuePair<string, string>[] Credentials() =>
    [
        new("username", TestAdmin.User),
        new("password", TestAdmin.Password),
        new("code", new Totp(Base32Encoding.ToBytes(TestAdmin.TotpSecret)).ComputeTotp(DateTime.UtcNow)),
    ];

    public static IEnumerable<KeyValuePair<string, string>> HiddenFields(string html) =>
        HiddenInput().Matches(html).Select(m => KeyValuePair.Create(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)));

    [GeneratedRegex("""<input type=hidden name="?([^" >]+)"? value="([^"]*)">""")]
    private static partial Regex HiddenInput();
}
