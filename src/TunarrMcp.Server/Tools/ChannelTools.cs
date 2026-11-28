using System.ComponentModel;
using ModelContextProtocol.Server;
using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr;

namespace TunarrMcp.Server.Tools;

[McpServerToolType]
public sealed class ChannelTools(TunarrClient tunarr)
{
    [McpServerTool(Name = "list_channels", Title = "List channels", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Every Tunarr channel: number, name, id, and whether it is programmed by hand or by a time-slot or random-slot schedule. Use the id with the other channel tools.")]
    public async Task<ChannelList> ListChannels(CancellationToken cancellationToken)
    {
        var channels = await tunarr.GetChannelsAsync(cancellationToken).ConfigureAwait(false);

        // One schedule request per channel. Home setups have a handful of channels, so no batching.
        var schedules = await Task.WhenAll(channels.Select(async c =>
                (c.Id, Schedule: await tunarr.GetChannelScheduleAsync(c.Id, cancellationToken).ConfigureAwait(false))))
            .ConfigureAwait(false);

        return ChannelShape.From(channels, schedules.ToDictionary(s => s.Id, s => s.Schedule));
    }
}
