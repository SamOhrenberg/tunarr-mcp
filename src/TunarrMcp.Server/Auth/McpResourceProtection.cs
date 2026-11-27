using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;
using TunarrMcp.Server.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TunarrMcp.Server.Auth;

/// <summary>
/// The resource-server side of Embedded mode: who may call /mcp.
///   - Tokens are validated in-process by OpenIddict against the local server's keys (no network hop), and
///     each token's database entry is checked too, so revoking a token takes effect immediately.
///   - The token's audience must be the MCP URL, so a token minted for anything else is refused.
///   - Unauthenticated calls get the SDK's 401, whose WWW-Authenticate header points at the RFC 9728
///     protected-resource metadata, where clients discover this server's authorization server.
/// </summary>
internal static class McpResourceProtection
{
    public const string RateLimitPolicy = "mcp";

    public static IServiceCollection AddMcpResourceProtection(this IServiceCollection services)
    {
        services.AddOpenIddict()
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseAspNetCore();
                validation.EnableTokenEntryValidation();
                validation.EnableAuthorizationEntryValidation();
            });

        services.AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddMcp();

        // Audience and metadata come from validated options, so they're applied here rather than inline.
        services.AddSingleton<IConfigureOptions<OpenIddictValidationOptions>, ConfigureValidation>();
        services.AddOptions<McpAuthenticationOptions>(McpAuthenticationDefaults.AuthenticationScheme)
            .Configure<IOptions<McpOptions>>((o, mcp) => o.ResourceMetadata = new()
            {
                Resource = mcp.Value.Resource.AbsoluteUri,
                AuthorizationServers = { mcp.Value.Issuer.AbsoluteUri }, // Claude only reads the first entry
                ScopesSupported = [AuthScopes.Read, AuthScopes.Write, Scopes.OfflineAccess],
                ResourceName = "Tunarr",
            });

        return services;
    }

    /// <summary>Requires a valid token on the MCP endpoint and applies the per-token rate limit.</summary>
    public static TBuilder RequireMcpAccessToken<TBuilder>(this TBuilder endpoint) where TBuilder : IEndpointConventionBuilder =>
        endpoint.RequireAuthorization().RequireRateLimiting(RateLimitPolicy);

    /// <summary>Rate-limit partition: the token, so one runaway client can't starve another.</summary>
    public static string PartitionKey(HttpContext context) =>
        context.User.FindFirst(Claims.Private.TokenId)?.Value
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";

    private sealed class ConfigureValidation(IOptions<McpOptions> mcp) : IConfigureOptions<OpenIddictValidationOptions>
    {
        public void Configure(OpenIddictValidationOptions options) => options.Audiences.Add(mcp.Value.Resource.AbsoluteUri);
    }
}
