using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Server.Auth;

internal static class AuthRegistration
{
    public static IServiceCollection AddTunarrMcpAuth(this IServiceCollection services, IConfiguration configuration)
    {
        return ModeOf(configuration) switch
        {
            AuthMode.Embedded => services.AddEmbeddedAuthServer(),
            AuthMode.ExternalOidc => throw new NotSupportedException("Auth:Mode=ExternalOidc is not implemented yet. Use Embedded."),
            _ => services,
        };
    }

    public static async Task UseTunarrMcpAuthAsync(this WebApplication app)
    {
        if (ModeOf(app.Configuration) == AuthMode.Embedded)
            await app.UseEmbeddedAuthServerAsync().ConfigureAwait(false);
    }

    /// <summary>Protects the MCP endpoint in Embedded mode. Auth:Mode=None leaves it open (validated to be opt-in).</summary>
    public static TBuilder RequireMcpAuthorization<TBuilder>(this TBuilder endpoint, IConfiguration configuration)
        where TBuilder : IEndpointConventionBuilder =>
        ModeOf(configuration) == AuthMode.Embedded ? endpoint.RequireMcpAccessToken() : endpoint;

    // Needed before options are built; the full section is validated on start.
    private static AuthMode ModeOf(IConfiguration configuration) =>
        configuration.GetSection(AuthOptions.Section).Get<AuthOptions>()?.Mode ?? AuthMode.Embedded;
}
