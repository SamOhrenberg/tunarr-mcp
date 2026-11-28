using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Shaping;

/// <param name="TotalItems">Items in the whole lineup, which loops when it reaches the end.</param>
/// <param name="NextOffset">Pass as offset for the next page; null on the last page.</param>
public sealed record ChannelLineupView(
    int Number,
    string Name,
    string Id,
    string Programming,
    int TotalItems,
    int Offset,
    int? NextOffset,
    IReadOnlyList<LineupItemView> Items);

/// <param name="Position">0-based position in the lineup.</param>
/// <param name="StartsAtHour">Where the item starts, in hours from the start of the lineup.</param>
/// <param name="Type">movie, episode, other_video, music_video, track, flex (a gap: offline screen or filler), or redirect (plays another channel).</param>
/// <param name="Id">Program id. The same program can appear several times.</param>
/// <param name="Missing">The media server no longer has the file.</param>
public sealed record LineupItemView(
    int Position,
    double StartsAtHour,
    string Type,
    string? Title,
    string? Show,
    int? SeasonNumber,
    int? EpisodeNumber,
    double Minutes,
    string? Id,
    bool? Missing);

internal static class LineupShape
{
    public static ChannelLineupView From(Channel channel, string programming, ChannelLineup lineup, int offset)
    {
        var items = lineup.Lineup
            .Select((item, i) => Item(item, offset + i, lineup.StartTimeOffsets?.ElementAtOrDefault(i) ?? 0, lineup.Programs))
            .ToArray();
        var end = offset + items.Length;

        return new ChannelLineupView(
            channel.Number,
            channel.Name,
            channel.Id,
            programming,
            lineup.TotalPrograms,
            offset,
            end < lineup.TotalPrograms && items.Length > 0 ? end : null,
            items);
    }

    private static LineupItemView Item(LineupItem item, int position, double startOffsetMs, IReadOnlyDictionary<string, LineupProgramEntry>? programs)
    {
        var minutes = Math.Round(item.Duration / TimeSpan.MillisecondsPerMinute, 1);
        var startsAt = Math.Round(startOffsetMs / TimeSpan.MillisecondsPerHour, 2);

        switch (item.Type)
        {
            case "flex":
                return new LineupItemView(position, startsAt, "flex", null, null, null, null, minutes, null, null);
            case "redirect":
                return new LineupItemView(position, startsAt, "redirect",
                    item.ChannelNumber is { } n ? $"{n} {item.ChannelName}" : item.ChannelName, null, null, null, minutes, null, null);
        }

        // content, custom and filler items point at a program.
        var program = item.Id is not null && programs?.TryGetValue(item.Id, out var entry) == true ? entry.Program : null;
        if (program is null)
            return new LineupItemView(position, startsAt, item.Type, null, null, null, null, minutes, item.Id, null);

        var isEpisode = program.Type == "episode";
        return new LineupItemView(
            position,
            startsAt,
            program.Type,
            program.Title,
            isEpisode ? program.Show?.Title : null,
            isEpisode ? program.Season?.Index : null,
            isEpisode ? program.EpisodeNumber : null,
            minutes,
            program.Uuid,
            program.State is not null and not "ok" ? true : null);
    }
}
