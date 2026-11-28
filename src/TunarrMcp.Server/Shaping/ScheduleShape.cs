using System.Globalization;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Shaping;

/// <param name="Programming">manual, time_slots or random_slots.</param>
/// <param name="Note">Set for manual channels, which have no slots.</param>
public sealed record ChannelScheduleView(
    int Number,
    string Name,
    string Id,
    string Programming,
    string? Note,
    ScheduleSettings? Settings,
    IReadOnlyList<SlotView> Slots);

/// <param name="Period">Time slots: day (slots repeat daily) or week.</param>
/// <param name="PadMinutes">Program start times are rounded up to multiples of this.</param>
/// <param name="FlexPreference">Where leftover time goes: distribute (between programs) or end (end of the slot).</param>
/// <param name="LatenessMinutes">Time slots: how late a program may start and still count for its slot.</param>
/// <param name="Distribution">Random slots: weighted (by slot weight), uniform, or none (slots in order).</param>
/// <param name="PadStyle">Random slots: slot (pad each slot) or episode (pad each program).</param>
/// <param name="MaxDays">How many days of lineup Tunarr generates from the schedule.</param>
/// <param name="RebuildEachCycle">Tunarr regenerates the lineup from the schedule each time it runs out.</param>
public sealed record ScheduleSettings(
    string? Period,
    double? PadMinutes,
    string? FlexPreference,
    double? LatenessMinutes,
    string? Distribution,
    string? PadStyle,
    int? MaxDays,
    bool? RebuildEachCycle);

/// <summary>One slot. Which fields are set depends on the schedule type and what the slot plays.</summary>
/// <param name="At">Time slots: start time, HH:mm in the server's time zone; prefixed with the day for weekly schedules.</param>
/// <param name="Weight">Random slots: relative chance of being picked.</param>
/// <param name="Length">Random slots: how long the slot runs, e.g. "1 program" or "30 min".</param>
/// <param name="CooldownMinutes">Random slots: minimum time before the slot can play again.</param>
/// <param name="Plays">show, custom_show, smart_collection, filler, flex (dead air / offline screen) or redirect (another channel).</param>
/// <param name="Id">The show, custom show, smart collection, filler list or channel id.</param>
/// <param name="Order">How the slot works through its content: next (in order, picking up where it left off), shuffle, ordered_shuffle, alphanumeric or chronological. " (descending)" when reversed.</param>
/// <param name="Filler">Filler around the slot's programs: "list name: when" (pre, post, head, tail, mid, fallback).</param>
/// <param name="Missing">The thing the slot refers to no longer exists; the slot airs as flex.</param>
public sealed record SlotView(
    string? At,
    double? Weight,
    string? Length,
    double? CooldownMinutes,
    string Plays,
    string? Title,
    string? Id,
    string? Order,
    IReadOnlyList<int>? OnlySeasons,
    IReadOnlyList<int>? ExceptSeasons,
    IReadOnlyList<string>? Filler,
    bool? MidRollBreaks,
    bool? Missing);

internal static class ScheduleShape
{
    private static readonly string[] Days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public static ChannelScheduleView From(Channel channel, ChannelScheduleResponse? response, IReadOnlyDictionary<string, string> fillerNames)
    {
        var programming = ChannelShape.Programming(response);
        if (response?.Schedule is not { } schedule)
        {
            return new ChannelScheduleView(channel.Number, channel.Name, channel.Id, programming,
                "Programmed by hand, with no slot schedule. get_channel_lineup shows what it plays.", null, []);
        }

        var isTime = schedule.Type == "time";
        var weekly = schedule.Period == "week";
        var slots = isTime ? schedule.Slots.OrderBy(s => s.StartTime ?? 0) : schedule.Slots.AsEnumerable();

        return new ChannelScheduleView(
            channel.Number,
            channel.Name,
            channel.Id,
            programming,
            null,
            new ScheduleSettings(
                isTime ? schedule.Period : null,
                Minutes(schedule.PadMs),
                schedule.FlexPreference,
                isTime ? Minutes(schedule.LatenessMs) : null,
                isTime ? null : schedule.RandomDistribution,
                isTime ? null : schedule.PadStyle,
                schedule.MaxDays,
                response.RebuildEachCycle),
            slots.Select(s => Slot(s, isTime, weekly, fillerNames)).ToArray());
    }

    private static SlotView Slot(ScheduleSlot slot, bool isTime, bool weekly, IReadOnlyDictionary<string, string> fillerNames)
    {
        var (plays, title, id) = slot.Type switch
        {
            "show" => ("show", slot.Show?.Title ?? slot.MissingShow?.Title, slot.ShowId),
            "custom-show" => ("custom_show", slot.CustomShow?.Name, slot.CustomShowId),
            "smart-collection" => ("smart_collection", slot.SmartCollection?.Name, slot.SmartCollectionId),
            "filler" => ("filler", slot.FillerList?.Name ?? Name(slot.FillerListId, fillerNames), slot.FillerListId),
            "redirect" => ("redirect", slot.Channel?.Name ?? slot.ChannelName, slot.ChannelId),
            "flex" => ("flex", null, null),
            var other => (other, null, null),
        };

        var missing = slot.IsMissing == true || slot.MissingShow is not null || (slot.Type == "show" && slot.Show is null);

        return new SlotView(
            isTime ? At(slot.StartTime ?? 0, weekly) : null,
            isTime ? null : slot.Weight,
            isTime ? null : Length(slot.DurationSpec),
            isTime ? null : Minutes(slot.CooldownMs),
            plays,
            title,
            id,
            Order(slot),
            NullIfEmpty(slot.SeasonFilter),
            NullIfEmpty(slot.SeasonExcludeFilter),
            slot.Filler is { Count: > 0 } filler
                ? filler.Select(f => $"{Name(f.FillerListId, fillerNames)}: {string.Join(", ", f.Types)}").ToArray()
                : null,
            slot.MidRoll is null ? null : true,
            missing ? true : null);
    }

    /// <summary>"07:00", or "Mon 07:00" for weekly schedules (offset 0 is Sunday midnight).</summary>
    internal static string At(double startTimeMs, bool weekly)
    {
        var offset = TimeSpan.FromMilliseconds(startTimeMs);
        var time = new TimeOnly(offset.Hours, offset.Minutes).ToString("HH:mm", CultureInfo.InvariantCulture);
        return weekly ? $"{Days[offset.Days % 7]} {time}" : time;
    }

    internal static string? Length(SlotDurationSpec? spec) => spec switch
    {
        { Type: "fixed", DurationMs: { } ms } => $"{Minutes(ms)} min",
        { Type: "dynamic", ProgramCount: 1 } => "1 program",
        { Type: "dynamic", ProgramCount: { } n } => $"{n} programs",
        null => "1 program", // Tunarr's default
        _ => null,
    };

    private static string? Order(ScheduleSlot slot) => slot.Order switch
    {
        null => null,
        var order when slot.Direction == "desc" => order + " (descending)",
        var order => order,
    };

    private static string Name(string? id, IReadOnlyDictionary<string, string> names) =>
        id is not null && names.TryGetValue(id, out var name) ? name : id ?? "unknown";

    private static double? Minutes(double? ms) => ms is { } v ? Math.Round(v / TimeSpan.MillisecondsPerMinute, 1) : null;

    private static IReadOnlyList<int>? NullIfEmpty(IReadOnlyList<int>? list) => list is { Count: > 0 } ? list : null;
}
