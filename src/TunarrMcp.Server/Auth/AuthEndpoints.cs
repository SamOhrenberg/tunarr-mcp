using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using TunarrMcp.Server.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TunarrMcp.Server.Auth;

/// <summary>
/// The interactive part of the embedded authorization server:
///   GET  /connect/authorize  → not signed in? redirect to /login, else show consent
///   GET  /login, POST /login → password + TOTP, then back to /connect/authorize
///   POST /connect/authorize  → consent decision; approve issues an authorization code via OpenIddict
///   POST /connect/token      → code and refresh grants (OpenIddict validates; this re-checks the subject)
/// OpenIddict validates every OAuth parameter (client, redirect_uri, PKCE, resource) before these handlers run.
/// </summary>
internal static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    // v1 has one admin and doesn't check scopes per tool; Tools:Mode is the gate. offline_access is always
    // granted so clients get a refresh token, whether or not they asked for one.
    private static readonly string[] GrantedScopes = [AuthScopes.Read, AuthScopes.Write, Scopes.OfflineAccess];

    public static IEndpointRouteBuilder MapEmbeddedAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/login", LoginPage);
        app.MapPost("/login", Login).RequireRateLimiting(LoginRateLimitPolicy);
        app.MapMethods("/connect/authorize", [HttpMethods.Get, HttpMethods.Post], Authorize);
        app.MapPost("/connect/token", Token);
        return app;
    }

    private static IResult LoginPage(HttpContext context, IAntiforgery antiforgery, string? returnUrl) =>
        AuthPages.Login(context, antiforgery.GetAndStoreTokens(context), SafeReturnUrl(returnUrl), error: null);

    private static async Task<IResult> Login(HttpContext context, IAntiforgery antiforgery, AdminLogin admin, ILogger<AdminLogin> logger)
    {
        if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
            return Results.BadRequest();

        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var returnUrl = SafeReturnUrl(form["returnUrl"]);
        var username = form["username"].ToString();

        var remoteIp = context.Connection.RemoteIpAddress;
        switch (admin.Verify(username, form["password"], form["code"]))
        {
            case LoginResult.Success:
                AuthLog.SignedIn(logger, remoteIp);
                var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], AdminCookie.Scheme);
                await context.SignInAsync(AdminCookie.Scheme, new ClaimsPrincipal(identity)).ConfigureAwait(false);
                return Results.Redirect(returnUrl);

            case LoginResult.LockedOut:
                AuthLog.LockedOutAttempt(logger, remoteIp);
                return AuthPages.Login(context, antiforgery.GetAndStoreTokens(context), returnUrl, "Too many failed attempts. Try again later.");

            default:
                AuthLog.FailedLogin(logger, remoteIp);
                return AuthPages.Login(context, antiforgery.GetAndStoreTokens(context), returnUrl, "Wrong username, password or code.");
        }
    }

    private static async Task<IResult> Authorize(
        HttpContext context, IAntiforgery antiforgery, IOpenIddictApplicationManager applications,
        IOptions<AuthOptions> auth, IOptions<McpOptions> mcp)
    {
        var request = context.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("OpenIddict did not process this authorization request.");

        // Consent is never remembered, so a request that forbids any UI can't succeed.
        if (request.HasPromptValue(PromptValues.None))
            return Forbid(Errors.ConsentRequired, "This server always asks for consent.");

        var session = await context.AuthenticateAsync(AdminCookie.Scheme).ConfigureAwait(false);
        if (!session.Succeeded || session.Principal.Identity?.Name != auth.Value.AdminUser)
        {
            // Only the initial GET can be resumed after login: its parameters are in the URL.
            if (!HttpMethods.IsGet(context.Request.Method))
                return Forbid(Errors.LoginRequired, "Sign in again and retry.");

            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = context.Request.PathBase + context.Request.Path + context.Request.QueryString },
                [AdminCookie.Scheme]);
        }

        var application = await applications.FindByClientIdAsync(request.ClientId!).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("OpenIddict accepted an unknown client.");

        if (HttpMethods.IsGet(context.Request.Method))
        {
            var clientName = await applications.GetDisplayNameAsync(application).ConfigureAwait(false) ?? request.ClientId!;
            return AuthPages.Consent(context, antiforgery.GetAndStoreTokens(context), clientName,
                new Uri(request.RedirectUri!), FormParameters(request));
        }

        if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
            return Results.BadRequest();

        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        if (form["decision"] != "approve")
            return Forbid(Errors.AccessDenied, "The request was denied.");

        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, auth.Value.AdminUser).SetClaim(Claims.Name, auth.Value.AdminUser);
        identity.SetScopes(GrantedScopes);
        identity.SetResources(mcp.Value.Resource.AbsoluteUri); // token audience = the MCP endpoint
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> Token(HttpContext context, IOptions<AuthOptions> auth)
    {
        var request = context.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("OpenIddict did not process this token request.");
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            throw new InvalidOperationException("OpenIddict accepted a grant type this server doesn't enable.");

        // The principal stored in the code or refresh token, already validated by OpenIddict.
        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme).ConfigureAwait(false);
        if (result.Principal is not { } principal || principal.GetClaim(Claims.Subject) != auth.Value.AdminUser)
            return Forbid(Errors.InvalidGrant, "The admin account changed. Sign in again.");

        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>The authorization request's parameters, re-posted as hidden fields by the consent form.</summary>
    private static IEnumerable<KeyValuePair<string, string>> FormParameters(OpenIddictRequest request) =>
        from parameter in request.GetParameters()
        from value in parameter.Value.GetRawValue() switch
        {
            string single => [single],
            string?[] many => many.OfType<string>(),
            _ => [],
        }
        select KeyValuePair.Create(parameter.Key, value);

    /// <summary>Login only ever returns to the authorization endpoint, so it can't be used as an open redirect.</summary>
    private static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is not null && returnUrl.StartsWith("/connect/authorize?", StringComparison.Ordinal) && !returnUrl.Contains('\\', StringComparison.Ordinal)
            ? returnUrl
            : "/connect/authorize";

    private static IResult Forbid(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
