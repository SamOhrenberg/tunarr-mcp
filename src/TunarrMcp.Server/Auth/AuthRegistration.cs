using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Server.Auth;

internal static class AuthRegistration
{
    public static IServiceCollection AddTunarrMcpAuth(this IServiceCollection services, IConfiguration configuration)
    {
        // Needed at registration time; the full section is validated on start.
        var mode = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>()?.Mode ?? AuthMode.Embedded;

        return mode switch
        {
            AuthMode.Embedded => services.AddEmbeddedAuthServer(),
            AuthMode.ExternalOidc => throw new NotSupportedException("Auth:Mode=ExternalOidc is not implemented yet. Use Embedded."),
            _ => services,
        };
    }
}
