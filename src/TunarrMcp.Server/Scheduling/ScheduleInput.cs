using System.ComponentModel;

namespace TunarrMcp.Server.Scheduling;

/// <summary>
/// A slot schedule as a tool argument. It mirrors what get_channel_schedule returns, so a schedule can be read,
/// changed and sent back. Settings left out keep the channel's current value (when it already has a schedule of
/// the same type), else Tunarr's defaults.
/// </summary>
public sealed record ScheduleInput(
    [property: Description("time (slots start at fixed times of the day or week) or random (weighted slots picked at random).")]
    string Type,
    [property: Description("The slots. Time slots: one per start time. Random slots: in order of index.")]
    IReadOnlyList<SlotInput> Slots,
    [property: Description("Time slots: day (the slots repeat daily) or week (each slot names its day).")]
    string? Period = null,
    [property: Description("Program start times are rounded up to multiples of this many minutes, e.g. 5 or 30. 0 for none.")]
    double? PadMinutes = null,
    [property: Description("Where leftover time goes: distribute (spread between programs) or end (at the end of the slot).")]
    string? FlexPreference = null,
    [property: Description("Days of lineup Tunarr generates from the schedule before it loops.")]
    int? MaxDays = null,
    [property: Description("Time slots: minutes a program may start late and still play in its slot.")]
    double? LatenessMinutes = null,
    [property: Description("Random slots: weighted (by each slot's weight), uniform (equal chance) or none (slots in order).")]
    string? Distribution = null,
    [property: Description("Random slots: slot (pad each slot's start) or episode (pad each program).")]
    string? PadStyle = null);

/// <summary>One slot. Which fields apply depends on the schedule type and what the slot plays.</summary>
public sealed record SlotInput(
    [property: Description("show, custom_show (also how movies get into a slot), smart_collection, filler, flex (offline) or redirect (another channel).")]
    string Plays,
    [property: Description("What it plays: a show, custom show, smart collection or filler list id, or for redirect a channel number, name or id. Not used for flex.")]
    string? Id = null,
    [property: Description("Time slots: start time, 24-hour HH:mm, e.g. 19:30; weekly schedules prefix the day, e.g. Mon 19:30.")]
    string? At = null,
    [property: Description("Random slots: relative chance of being picked. Default 1.")]
    double? Weight = null,
    [property: Description("Random slots: the slot plays this many programs. Default 1. Give this or minutes.")]
    int? Programs = null,
    [property: Description("Random slots: the slot runs this many minutes. Required for flex and redirect.")]
    double? Minutes = null,
    [property: Description("Random slots: minutes before the slot can play again. Default 0.")]
    double? CooldownMinutes = null,
    [property: Description("Shows, custom shows, smart collections: next (in order, picking up where it left off; the default), shuffle, ordered_shuffle, alphanumeric or chronological. Filler slots: shuffle_prefer_short (default), shuffle_prefer_long or uniform.")]
    string? Order = null,
    [property: Description("Reverse the order (e.g. newest first for chronological).")]
    bool? Descending = null,
    [property: Description("Shows: play only these season numbers. [] for all.")]
    IReadOnlyList<int>? OnlySeasons = null,
    [property: Description("Shows: skip these season numbers.")]
    IReadOnlyList<int>? ExceptSeasons = null,
    [property: Description("Shows, custom shows, smart collections: filler to play around the slot's programs.")]
    IReadOnlyList<FillerInput>? Filler = null,
    [property: Description("To change an existing slot, its slotId from get_channel_schedule: settings this input doesn't cover (such as mid-roll breaks) are kept, and fields left out keep their current value. Leave out for a new slot.")]
    string? SlotId = null);

public sealed record FillerInput(
    [property: Description("Filler list id from list_collections.")]
    string FillerListId,
    [property: Description("When it plays: pre (before each program), post (after), head (start of the slot), tail (end of the slot), fallback (fills gaps).")]
    IReadOnlyList<string> When,
    [property: Description("shuffle_prefer_short (default), shuffle_prefer_long or uniform.")]
    string? Order = null);
