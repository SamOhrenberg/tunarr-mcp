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

/// <summary>A saved slot schedule. Type is "time" (slots at fixed times) or "random" (weighted slots).</summary>
public sealed record ChannelSchedule(string Type);
