using System.Text.Json;

namespace TunarrMcp.Tunarr.Models;

/// <summary>One entry of GET /channels. Duration is the lineup's length in ms; StartTime is when it first aired (epoch ms).</summary>
public sealed record Channel(
    string Id,
    int Number,
    string Name,
    string? GroupTitle,
    double Duration,
    int ProgramCount,
    long StartTime,
    bool Stealth,
    ChannelOnDemand? OnDemand);

public sealed record ChannelOnDemand(bool Enabled);

/// <summary>
/// GET /channels/:id/schedule. Tunarr answers 200 with an empty body for a channel without a slot schedule
/// (a hand-built lineup), so the client maps that to null rather than to this type.
/// </summary>
public sealed record ChannelScheduleResponse(ChannelSchedule? Schedule, bool? RebuildEachCycle);

/// <summary>
/// A saved slot schedule. Type is "time" (slots at fixed times) or "random" (weighted slots); some settings
/// belong to only one of them. Durations are in ms.
/// </summary>
public sealed record ChannelSchedule(string Type)
{
    public IReadOnlyList<ScheduleSlot> Slots { get; init; } = [];

    /// <summary>Days of lineup Tunarr generates from the schedule.</summary>
    public int? MaxDays { get; init; }

    /// <summary>Program start times are padded to multiples of this.</summary>
    public double? PadMs { get; init; }

    /// <summary>distribute (spread flex time between programs) or end (put it at the end of a slot).</summary>
    public string? FlexPreference { get; init; }

    /// <summary>Time slots: day or week.</summary>
    public string? Period { get; init; }

    /// <summary>Time slots: how late a program may start and still air in its slot.</summary>
    public double? LatenessMs { get; init; }

    /// <summary>Random slots: uniform, weighted or none.</summary>
    public string? RandomDistribution { get; init; }

    /// <summary>Random slots: slot (pad each slot) or episode (pad each program).</summary>
    public string? PadStyle { get; init; }
}

/// <summary>
/// One slot of a saved schedule, as GET /channels/:id/schedule returns it: the slot itself plus the thing it
/// refers to (Show, CustomShow, FillerList, SmartCollection, Channel), which Tunarr adds for display.
/// </summary>
public sealed record ScheduleSlot(string Type)
{
    public string? Id { get; init; }

    /// <summary>Time slots: offset from the start of the day or week (Sunday 00:00), in Tunarr's time zone.</summary>
    public double? StartTime { get; init; }

    public string? ShowId { get; init; }
    public string? CustomShowId { get; init; }
    public string? FillerListId { get; init; }
    public string? SmartCollectionId { get; init; }
    public string? ChannelId { get; init; }
    public string? ChannelName { get; init; }

    /// <summary>next, shuffle, ordered_shuffle, alphanumeric or chronological (fillerlist slots: shuffle_prefer_short etc.).</summary>
    public string? Order { get; init; }

    /// <summary>asc or desc.</summary>
    public string? Direction { get; init; }

    public IReadOnlyList<int>? SeasonFilter { get; init; }
    public IReadOnlyList<int>? SeasonExcludeFilter { get; init; }
    public IReadOnlyList<SlotFiller>? Filler { get; init; }

    /// <summary>Present when the slot breaks long programs up with filler.</summary>
    public JsonElement? MidRoll { get; init; }

    /// <summary>Random slots: relative chance of being picked.</summary>
    public double? Weight { get; init; }

    /// <summary>Random slots: minimum time before the slot can be picked again.</summary>
    public double? CooldownMs { get; init; }

    /// <summary>Random slots: how long the slot runs.</summary>
    public SlotDurationSpec? DurationSpec { get; init; }

    public SlotTarget? Show { get; init; }
    public SlotTarget? CustomShow { get; init; }
    public SlotTarget? FillerList { get; init; }
    public SlotTarget? SmartCollection { get; init; }
    public SlotTarget? Channel { get; init; }

    /// <summary>Set when the show the slot refers to is gone from the library.</summary>
    public SlotTarget? MissingShow { get; init; }

    public bool? IsMissing { get; init; }
}

/// <summary>Types is when the filler plays: head, pre, post, tail, fallback or mid.</summary>
public sealed record SlotFiller(IReadOnlyList<string> Types, string FillerListId, string? FillerOrder);

/// <summary>Type fixed (DurationMs) or dynamic (ProgramCount programs).</summary>
public sealed record SlotDurationSpec(string Type, double? DurationMs, int? ProgramCount);

/// <summary>What a slot refers to, as Tunarr embeds it: shows have a Title, the rest a Name.</summary>
public sealed record SlotTarget(string? Title, string? Name, int? Number);

/// <summary>
/// GET /channels/:id/programming?offset&amp;limit: a page of the lineup. Programs holds the details of the content
/// items on the page, keyed by lineup item id; StartTimeOffsets are each item's start within the lineup, in ms.
/// </summary>
public sealed record ChannelLineup(
    string Name,
    int Number,
    int TotalPrograms,
    IReadOnlyList<LineupItem> Lineup,
    IReadOnlyDictionary<string, LineupProgramEntry>? Programs,
    IReadOnlyList<double>? StartTimeOffsets);

/// <summary>Type is content, custom (from a custom show), filler, redirect (to another channel) or flex (a gap).</summary>
public sealed record LineupItem(string Type, string? Id, double Duration, string? ChannelName, int? ChannelNumber);

public sealed record LineupProgramEntry(LineupProgram? Program);

/// <summary>Type is movie, episode, other_video, music_video or track. State is "ok" or e.g. "missing".</summary>
public sealed record LineupProgram(
    string Uuid,
    string Type,
    string Title,
    int? Year,
    int? EpisodeNumber,
    SearchHitParent? Show,
    SearchHitParent? Season,
    string? State);
