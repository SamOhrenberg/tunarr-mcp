using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TunarrMcp.Server.Changes;
using TunarrMcp.Server.Scheduling;
using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr;

namespace TunarrMcp.Server.Tools;

/// <param name="Programming">time_slots or random_slots.</param>
/// <param name="GeneratedItems">Items in the lineup Tunarr generated from the schedule; it loops after that.</param>
public sealed record SetScheduleResult(int Channel, string Name, string Programming, int Slots, int GeneratedItems, string Note);

[McpServerToolType]
public sealed class ScheduleTools(TunarrClient tunarr, ChangeLog changes, WritableChannels writable, TimeProvider clock)
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

        return PreviewShape.From(target, Programming(schedule), preview, TimeSpan.FromHours(hours), clock);
    }

    [McpServerTool(Name = "set_channel_schedule", Title = "Save a channel's schedule", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("""
        Saves a time-slot or random-slot schedule for a channel, in the same form preview_schedule takes; Tunarr then
        replaces the channel's lineup with one generated from it. Preview first and show the result to the user.
        The schedule and lineup are snapshotted first: undo_last_change puts both back.
        """)]
    public async Task<SetScheduleResult> SetChannelSchedule(
        [Description("Channel number, name or id.")] string channel,
        [Description("The schedule.")] ScheduleInput schedule,
        [Description("""
            Needed when the channel is programmed by hand: its hand-built lineup is replaced by the generated one
            (undo_last_change brings it back).
            """)] bool replaceHandBuiltLineup = false,
        CancellationToken cancellationToken = default)
    {
        var channels = await tunarr.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        var target = ChannelLookup.Find(channels, channel);
        writable.EnsureWritable(target);

        return await changes.RunExclusiveAsync(async () =>
        {
            var current = await tunarr.GetChannelScheduleJsonAsync(target.Id, cancellationToken).ConfigureAwait(false);
            if (current is null && target.ProgramCount > 0 && !replaceHandBuiltLineup)
            {
                throw new McpException(
                    $"Channel {target.Number} {target.Name} is programmed by hand ({target.ProgramCount} items). Saving a schedule " +
                    "replaces that lineup with one generated from the schedule. Call again with replaceHandBuiltLineup: true " +
                    "to do it; undo_last_change can bring the old lineup back.");
            }

            var built = ScheduleBuilder.Build(schedule, current, channels, clock);
            var before = await tunarr.GetLineupItemsJsonAsync(target.Id, cancellationToken).ConfigureAwait(false);
            var saved = await tunarr.SaveScheduleAsync(target.Id, built, cancellationToken).ConfigureAwait(false);

            var slots = schedule.Slots.Count;
            var kind = schedule.Type == "time" ? "time" : "random";
            await changes.RecordAsync(new ChannelChange
            {
                ChannelId = target.Id,
                ChannelNumber = target.Number,
                ChannelName = target.Name,
                Tool = "set_channel_schedule",
                Summary = $"Saved a {kind}-slot schedule with {slots} slot{(slots == 1 ? "" : "s")}" +
                    (current is null ? ", replacing the hand-built lineup" : ""),
                Before = JsonSerializer.Serialize(new ChannelState(JsonSerializer.Deserialize<JsonElement>(before))
                {
                    ScheduleCaptured = true,
                    Schedule = current is null ? null : JsonSerializer.SerializeToElement(TunarrClient.StripScheduleDisplayFields(current)),
                }, JsonSerializerOptions.Web),
                AfterFingerprint = LineupFingerprint.Of(saved.Lineup),
            }, cancellationToken).ConfigureAwait(false);

            return new SetScheduleResult(target.Number, target.Name, Programming(schedule), slots, saved.TotalPrograms,
                "Saved; the guide updates shortly. get_channel_lineup shows the new lineup, undo_last_change reverts it.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string Programming(ScheduleInput schedule) => schedule.Type == "time" ? "time_slots" : "random_slots";
}
