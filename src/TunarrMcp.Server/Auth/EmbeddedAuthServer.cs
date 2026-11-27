using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using TunarrMcp.Server.Auth.Registration;
using TunarrMcp.Server.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TunarrMcp.Server.Auth;

/// <summary>
/// Auth:Mode=Embedded: an OAuth 2.1 authorization server inside this app (OpenIddict), for a single admin.
/// This sets up storage, keys, endpoints and metadata, and wires in the login/consent flow (AuthEndpoints)
/// and client registration (Registration/).
/// </summary>
internal static class EmbeddedAuthServer
{
    public const string DatabaseFile = "auth.db";

    public static IServiceCollection AddEmbeddedAuthServer(this IServiceCollection services)
    {
        services.AddDbContext<AuthDbContext>((sp, db) =>
        {
            var data = sp.GetRequiredService<IOptions<DataOptions>>().Value;
            db.UseSqlite($"Data Source={Path.Combine(data.Path, DatabaseFile)}");
            db.UseOpenIddict();
        });

        services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<AuthDbContext>())
            .AddServer(server =>
            {
                server.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    // RFC 8414 location first (what MCP clients look up), OIDC location as a fallback.
                    .SetConfigurationEndpointUris(".well-known/oauth-authorization-server", ".well-known/openid-configuration")
                    .SetJsonWebKeySetEndpointUris(".well-known/jwks");

                server.AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .RequireProofKeyForCodeExchange();

                server.RegisterScopes(AuthScopes.Read, AuthScopes.Write, Scopes.OfflineAccess);

                // Refresh tokens roll by default: each use issues a new one and a reused one gets invalid_grant.
                server.SetAccessTokenLifetime(TimeSpan.FromMinutes(15))
                    .SetRefreshTokenLifetime(TimeSpan.FromDays(30));

                server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();

                server.AdvertiseClientRegistration();
            });

        services.AddClientRegistration();

        // Issuer, resource and keys come from validated options, so they're applied here rather than above.
        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, ConfigureServer>();
        services.AddSingleton<IConfigureOptions<OpenIddictServerAspNetCoreOptions>, ConfigureServerAspNetCore>();

        // Login and consent: admin session cookie, CSRF tokens, credential check, per-IP rate limit.
        services.AddAuthentication().AddCookie(AdminCookie.Scheme, AdminCookie.Configure);
        services.AddAuthorization();
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "tunarr_mcp_csrf";
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        services.AddSingleton<AdminLogin>();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, context => PerIp(context, permits: 5, TimeSpan.FromMinutes(1)));
            // Claude registers once per connector setup; this only stops someone filling the database.
            o.AddPolicy(DynamicClientRegistration.RateLimitPolicy, context => PerIp(context, permits: 10, TimeSpan.FromHours(1)));
        });

        return services;
    }

    /// <summary>Creates the store, then adds the auth middleware and the login, consent and token endpoints.</summary>
    public static async Task UseEmbeddedAuthServerAsync(this WebApplication app)
    {
        Directory.CreateDirectory(app.Services.GetRequiredService<IOptions<DataOptions>>().Value.Path);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.EnsureCreatedAsync().ConfigureAwait(false);

        app.UseClientMetadataDocuments(); // before OpenIddict sees /connect/authorize
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapEmbeddedAuthEndpoints();
        app.MapDynamicClientRegistration();
    }

    private static RateLimitPartition<string> PerIp(HttpContext context, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 });

    private sealed class ConfigureServer(IOptions<McpOptions> mcp, IOptions<DataOptions> data) : IConfigureOptions<OpenIddictServerOptions>
    {
        public void Configure(OpenIddictServerOptions options)
        {
            options.Issuer = mcp.Value.Issuer;

            // Only the MCP endpoint may be requested as a token audience (RFC 8707 resource indicators).
            options.Resources.Add(mcp.Value.Resource);

            // OAuth 2.1 drops "plain" PKCE. Removing it here both hides it from metadata and rejects it.
            options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);

            var keys = Path.Combine(data.Value.Path, "keys");
            options.SigningCredentials.Add(new SigningCredentials(
                KeyStore.LoadOrCreate(keys, "signing"), SecurityAlgorithms.RsaSha256));
            options.EncryptionCredentials.Add(new EncryptingCredentials(
                KeyStore.LoadOrCreate(keys, "encryption"), SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        }
    }

    private sealed class ConfigureServerAspNetCore(IOptions<McpOptions> mcp) : IConfigureOptions<OpenIddictServerAspNetCoreOptions>
    {
        // TLS normally ends at the reverse proxy and forwarded headers restore the https scheme.
        // Plain http is only accepted when the public URL itself is http, which validation limits to localhost.
        public void Configure(OpenIddictServerAspNetCoreOptions options) =>
            options.DisableTransportSecurityRequirement = mcp.Value.Resource.Scheme == Uri.UriSchemeHttp;
    }
}
