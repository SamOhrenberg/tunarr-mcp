using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TunarrMcp.Server.Scheduling;
using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr;

namespace TunarrMcp.Server.Tools;

[McpServerToolType]
public sealed class ScheduleTools(TunarrClient tunarr, TimeProvider clock)
{
    private const int MaxPreviewHours = 48;

    [McpServerTool(Name = "preview_schedule", Title = "Preview a schedule", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Shows what a time-slot or random-slot schedule would air on a channel from now on, without saving anything:
        start times, titles and how much of the time is flex (offline). Tunarr checks the schedule and explains what's
        wrong with it. To change one slot of the current schedule, send the whole schedule from get_channel_schedule
        back with that slot changed, keeping each slot's slotId. Settings left out keep the channel's current values.
        """)]
    public async Task<SchedulePreviewView> PreviewSchedule(
        [Description("Channel number, name or id.")] string channel,
        [Description("The schedule.")] ScheduleInput schedule,
        [Description("Hours to show, from now. At most 48.")] int hours = 12,
        CancellationToken cancellationToken = default)
    {
        if (hours is < 1 or > MaxPreviewHours)
            throw new McpException($"hours must be between 1 and {MaxPreviewHours}.");

        var channels = await tunarr.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        var target = ChannelLookup.Find(channels, channel);
        var current = await tunarr.GetChannelScheduleJsonAsync(target.Id, cancellationToken).ConfigureAwait(false);

        var built = ScheduleBuilder.Build(schedule, current, channels, clock);
        var preview = await tunarr.PreviewScheduleAsync(target.Id, built, cancellationToken).ConfigureAwait(false);

        return PreviewShape.From(target, schedule.Type == "time" ? "time_slots" : "random_slots", preview, TimeSpan.FromHours(hours), clock);
    }
}
