using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using TunarrMcp.Server.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TunarrMcp.Server.Auth;

/// <summary>
/// Auth:Mode=Embedded: an OAuth 2.1 authorization server inside this app (OpenIddict), for a single admin.
/// This part sets up storage, keys, endpoints and metadata. The login/consent flow and client registration
/// are wired separately.
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
            });

        // Issuer, resource and keys come from validated options, so they're applied here rather than above.
        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, ConfigureServer>();
        services.AddSingleton<IConfigureOptions<OpenIddictServerAspNetCoreOptions>, ConfigureServerAspNetCore>();

        return services;
    }

    /// <summary>Creates the database (and data directory) if missing. Call once at startup, before serving.</summary>
    public static async Task InitializeAuthStoreAsync(this WebApplication app)
    {
        if (app.Services.GetService<IServiceProviderIsService>()?.IsService(typeof(AuthDbContext)) != true)
            return; // not Embedded mode

        Directory.CreateDirectory(app.Services.GetRequiredService<IOptions<DataOptions>>().Value.Path);

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.EnsureCreatedAsync().ConfigureAwait(false);
    }

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
