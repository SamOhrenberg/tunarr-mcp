using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr;

namespace TunarrMcp.Server.Tools;

[McpServerToolType]
public sealed class ChannelTools(TunarrClient tunarr)
{
    private const int MaxLineupPage = 200;

    [McpServerTool(Name = "list_channels", Title = "List channels", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Every Tunarr channel: number, name, id, and whether it is programmed by hand or by a time-slot or random-slot schedule. The other channel tools take a channel's number, name or id.")]
    public async Task<ChannelList> ListChannels(CancellationToken cancellationToken)
    {
        var channels = await tunarr.GetChannelsAsync(cancellationToken).ConfigureAwait(false);

        // One schedule request per channel. Home setups have a handful of channels, so no batching.
        var schedules = await Task.WhenAll(channels.Select(async c =>
                (c.Id, Schedule: await tunarr.GetChannelScheduleAsync(c.Id, cancellationToken).ConfigureAwait(false))))
            .ConfigureAwait(false);

        return ChannelShape.From(channels, schedules.ToDictionary(s => s.Id, s => s.Schedule));
    }

    [McpServerTool(Name = "get_channel_schedule", Title = "Get a channel's schedule", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        A channel's slot schedule in plain terms: for time slots, what plays at which time of day (or week); for
        random slots, each slot's weight, length and cooldown. Each slot names what it plays (show, custom show,
        smart collection, filler, flex or another channel), its order, season filters and filler. Channels
        programmed by hand have no slots; use get_channel_lineup for those.
        """)]
    public async Task<ChannelScheduleView> GetChannelSchedule(
        [Description("Channel number, name or id.")] string channel,
        CancellationToken cancellationToken = default)
    {
        var found = await ChannelLookup.FindAsync(tunarr, channel, cancellationToken).ConfigureAwait(false);

        var schedule = tunarr.GetChannelScheduleAsync(found.Id, cancellationToken);
        var fillerLists = tunarr.GetFillerListsAsync(cancellationToken);
        await Task.WhenAll(schedule, fillerLists).ConfigureAwait(false);

        return ScheduleShape.From(found, await schedule.ConfigureAwait(false),
            (await fillerLists.ConfigureAwait(false)).ToDictionary(f => f.Id, f => f.Name));
    }

    [McpServerTool(Name = "get_channel_lineup", Title = "Get a channel's lineup", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        The programs a channel plays, in order, a page at a time: title (and show, season and episode for
        episodes), runtime and where each starts in the lineup. The lineup loops when it reaches the end. For a
        slot-scheduled channel this is what Tunarr generated from the schedule; get_channel_schedule shows the
        schedule itself.
        """)]
    public async Task<ChannelLineupView> GetChannelLineup(
        [Description("Channel number, name or id.")] string channel,
        [Description("0-based position to start from.")] int offset = 0,
        [Description("Items per page, at most 200.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
            throw new McpException("offset can't be negative.");
        if (limit is < 1 or > MaxLineupPage)
            throw new McpException($"limit must be between 1 and {MaxLineupPage}.");

        var found = await ChannelLookup.FindAsync(tunarr, channel, cancellationToken).ConfigureAwait(false);

        var lineup = tunarr.GetChannelLineupAsync(found.Id, offset, limit, cancellationToken);
        var schedule = tunarr.GetChannelScheduleAsync(found.Id, cancellationToken);
        await Task.WhenAll(lineup, schedule).ConfigureAwait(false);

        var page = await lineup.ConfigureAwait(false)
            ?? throw new McpException($"Tunarr has no lineup for channel {found.Number} {found.Name}.");

        return LineupShape.From(found, ChannelShape.Programming(await schedule.ConfigureAwait(false)), page, offset);
    }
}
