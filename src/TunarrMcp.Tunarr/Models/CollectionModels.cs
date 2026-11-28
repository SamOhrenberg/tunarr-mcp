using System.Diagnostics.CodeAnalysis;

namespace TunarrMcp.Tunarr.Models;

/// <summary>
/// One entry of GET /custom-shows: a hand-picked, ordered list of programs. SchedulableContentCount leaves out
/// items that can't air (no duration, or gone from the media server). TotalDuration is in ms.
/// </summary>
public sealed record CustomShow(string Id, string Name, int ContentCount, int? SchedulableContentCount, double TotalDuration);

/// <summary>One entry of GET /filler-lists: short videos (bumpers, commercials) that fill gaps between programs.</summary>
public sealed record FillerList(string Id, string Name, int ContentCount);

/// <summary>One entry of GET /smart_collections: a saved library search. FilterString is the search in Tunarr's query syntax.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Tunarr's name for it.")]
public sealed record SmartCollection(string Uuid, string Name, string? FilterString, string? Keywords);
