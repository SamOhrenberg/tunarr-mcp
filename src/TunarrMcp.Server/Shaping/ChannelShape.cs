using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Shaping;

public sealed record ChannelList(IReadOnlyList<ChannelSummary> Channels);

/// <param name="Programming">
/// manual (a hand-built lineup), time_slots (shows at fixed times of the day or week) or
/// random_slots (weighted blocks in random order).
/// </param>
/// <param name="Programs">Items in the current lineup.</param>
/// <param name="LineupHours">How long the lineup runs before it repeats.</param>
/// <param name="HiddenFromGuide">Tunarr's "stealth" flag: the channel streams but is left out of the guide and M3U.</param>
public sealed record ChannelSummary(
    int Number,
    string Name,
    string Id,
    string? Group,
    string Programming,
    int Programs,
    double LineupHours,
    bool OnDemand,
    bool HiddenFromGuide);

internal static class ChannelShape
{
    public static ChannelList From(IReadOnlyList<Channel> channels, IReadOnlyDictionary<string, ChannelScheduleResponse?> schedules) =>
        new(channels
            .OrderBy(c => c.Number)
            .Select(c => new ChannelSummary(
                c.Number,
                c.Name,
                c.Id,
                string.IsNullOrWhiteSpace(c.GroupTitle) ? null : c.GroupTitle,
                Programming(schedules.GetValueOrDefault(c.Id)),
                c.ProgramCount,
                Math.Round(c.Duration / TimeSpan.MillisecondsPerHour, 1),
                c.OnDemand?.Enabled ?? false,
                c.Stealth))
            .ToArray());

    internal static string Programming(ChannelScheduleResponse? schedule) => schedule?.Schedule?.Type switch
    {
        "time" => "time_slots",
        "random" => "random_slots",
        null => "manual",
        // A schedule type newer than this server: say what Tunarr calls it rather than guess.
        var other => other,
    };
}
