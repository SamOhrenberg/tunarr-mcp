using System.ComponentModel;
using ModelContextProtocol.Server;
using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr;

namespace TunarrMcp.Server.Tools;

[McpServerToolType]
public sealed class SystemTools(TunarrClient tunarr, TimeProvider clock)
{
    [McpServerTool(Name = "system_status", Title = "Tunarr system status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Tunarr's version, any failing health checks, the state of its scheduled tasks (library scans, guide rebuilds, backups) and how far ahead the TV guide is built.")]
    public async Task<SystemStatus> SystemStatus(CancellationToken cancellationToken)
    {
        var version = tunarr.GetVersionAsync(cancellationToken);
        var health = tunarr.GetHealthAsync(cancellationToken);
        var tasks = tunarr.GetTasksAsync(cancellationToken);
        var guide = tunarr.GetGuideStatusAsync(cancellationToken);
        await Task.WhenAll(version, health, tasks, guide).ConfigureAwait(false);

        return SystemStatusShape.From(await version.ConfigureAwait(false), await health.ConfigureAwait(false),
            await tasks.ConfigureAwait(false), await guide.ConfigureAwait(false), clock);
    }
}
