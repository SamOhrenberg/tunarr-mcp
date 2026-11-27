using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using TunarrMcp.Server.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TunarrMcp.Server.Auth.Registration;

/// <summary>
/// RFC 7591 dynamic client registration, the fallback for clients that don't use metadata documents.
/// Anyone may register, but only public clients with allowed redirect URIs; a registration alone grants nothing,
/// since every authorization still needs the admin's login and consent.
/// </summary>
internal static class DynamicClientRegistration
{
    public const string RateLimitPolicy = "register";
    public const string Path = "/register";
    private const int MaxBodyBytes = 16 * 1024;
    private const int MaxRedirectUris = 10;
    private const int MaxClientNameLength = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] SupportedGrantTypes = [GrantTypes.AuthorizationCode, GrantTypes.RefreshToken];
    private static readonly string[] SupportedResponseTypes = [ResponseTypes.Code];

    public static IEndpointRouteBuilder MapDynamicClientRegistration(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, RegisterAsync).RequireRateLimiting(RateLimitPolicy);
        return app;
    }

    internal sealed record RegistrationRequest(
        string[]? RedirectUris,
        string? ClientName,
        string[]? GrantTypes,
        string[]? ResponseTypes,
        string? TokenEndpointAuthMethod);

    internal sealed record RegistrationResponse(
        string ClientId,
        long ClientIdIssuedAt,
        string? ClientName,
        string[] RedirectUris,
        string[] GrantTypes,
        string[] ResponseTypes,
        string TokenEndpointAuthMethod);

    private static async Task<IResult> RegisterAsync(
        HttpContext context, IOpenIddictApplicationManager applications, RedirectUriPolicy redirects,
        IOptions<McpOptions> mcp, TimeProvider clock)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxBodyBytes;

        RegistrationRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<RegistrationRequest>(Json, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return Error("invalid_client_metadata", "The body must be a JSON client metadata object (RFC 7591).");
        }

        if (request is null)
            return Error("invalid_client_metadata", "The body must be a JSON client metadata object (RFC 7591).");

        if (Validate(request, redirects, out var redirectUris) is { } error)
            return error;

        var clientName = request.ClientName?.Trim() is { Length: > 0 } name
            ? name[..Math.Min(name.Length, MaxClientNameLength)]
            : null;
        var clientId = "dcr_" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(18));

        var descriptor = McpClientDescriptor.Create(clientId, clientName, redirectUris, mcp.Value.Resource);
        descriptor.Properties[McpClientDescriptor.RegistrationProperty] = JsonSerializer.SerializeToElement("dcr");
        await applications.CreateAsync(descriptor, context.RequestAborted).ConfigureAwait(false);

        var response = new RegistrationResponse(
            clientId,
            clock.GetUtcNow().ToUnixTimeSeconds(),
            clientName,
            redirectUris.Select(u => u.AbsoluteUri).ToArray(),
            SupportedGrantTypes,
            SupportedResponseTypes,
            ClientAuthenticationMethods.None);

        return Results.Json(response, Json, statusCode: StatusCodes.Status201Created);
    }

    private static IResult? Validate(RegistrationRequest request, RedirectUriPolicy redirects, out Uri[] redirectUris)
    {
        redirectUris = [];

        if (request.RedirectUris is not { Length: > 0 and <= MaxRedirectUris } raw)
            return Error("invalid_redirect_uri", $"redirect_uris must list 1 to {MaxRedirectUris} URIs.");

        var parsed = new List<Uri>();
        foreach (var value in raw)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !redirects.IsAllowed(uri))
                return Error("invalid_redirect_uri", $"This server does not accept the redirect URI '{value}'.");
            parsed.Add(uri);
        }

        if (request.TokenEndpointAuthMethod is not (null or ClientAuthenticationMethods.None))
            return Error("invalid_client_metadata", "Only public clients are supported (token_endpoint_auth_method \"none\").");
        if (request.GrantTypes is { } grants && grants.Except(SupportedGrantTypes).Any())
            return Error("invalid_client_metadata", "Only the authorization_code and refresh_token grant types are supported.");
        if (request.ResponseTypes is { } responses && responses.Except(SupportedResponseTypes).Any())
            return Error("invalid_client_metadata", "Only the \"code\" response type is supported.");

        redirectUris = [.. parsed];
        return null;
    }

    private static IResult Error(string error, string description) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description }, statusCode: StatusCodes.Status400BadRequest);
}
