using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using TunarrMcp.Server.Configuration;
using TunarrMcp.Tunarr;

namespace TunarrMcp.Server.Hosting;

internal static class TunarrClientRegistration
{
    public static IServiceCollection AddTunarrClient(this IServiceCollection services)
    {
        services.AddHttpClient<TunarrClient>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<TunarrOptions>>().Value;
                http.BaseAddress = options.BaseUrl;

                if (options.UsesBasicAuth)
                {
                    var credentials = Encoding.UTF8.GetBytes($"{options.BasicAuthUser}:{options.BasicAuthPassword}");
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentials));
                }
            })
            .AddStandardResilienceHandler(resilience =>
            {
                // Writes are not idempotent (POST /channels twice makes two channels), so only safe methods retry.
                resilience.Retry.DisableForUnsafeHttpMethods();
            });

        services.AddHealthChecks()
            .AddCheck<TunarrHealthCheck>("tunarr", tags: [TunarrHealthCheck.ReadyTag], timeout: TimeSpan.FromSeconds(5));

        return services;
    }
}
