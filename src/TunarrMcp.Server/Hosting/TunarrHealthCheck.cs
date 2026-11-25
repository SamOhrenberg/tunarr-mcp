using Microsoft.Extensions.Diagnostics.HealthChecks;
using TunarrMcp.Tunarr;

namespace TunarrMcp.Server.Hosting;

/// <summary>
/// Readiness: Tunarr's API answers. Tunarr's own check results (warnings about missing season numbers and so on)
/// don't make this server unready, since the tools still work.
/// Any exception (network, non-2xx, resilience timeout or open circuit) is reported as Unhealthy by the
/// health check service, which logs it; the HTTP response itself only says "Unhealthy".
/// </summary>
internal sealed class TunarrHealthCheck(TunarrClient tunarr) : IHealthCheck
{
    public const string ReadyTag = "ready";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await tunarr.GetHealthAsync(cancellationToken).ConfigureAwait(false);
        return HealthCheckResult.Healthy();
    }
}
