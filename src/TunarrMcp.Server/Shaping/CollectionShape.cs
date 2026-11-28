using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Shaping;

public sealed record CollectionList(
    IReadOnlyList<CustomShowSummary> CustomShows,
    IReadOnlyList<FillerListSummary> FillerLists,
    IReadOnlyList<SmartCollectionSummary> SmartCollections);

/// <param name="Unschedulable">Items that can't air (no runtime, or gone from the media server); left out when 0.</param>
public sealed record CustomShowSummary(string Id, string Name, int Items, int? Unschedulable, double Hours);

public sealed record FillerListSummary(string Id, string Name, int Items);

/// <param name="Filter">The saved search, in Tunarr's query syntax.</param>
public sealed record SmartCollectionSummary(string Id, string Name, string? Filter);

internal static class CollectionShape
{
    public static CollectionList From(
        IReadOnlyList<CustomShow> customShows,
        IReadOnlyList<FillerList> fillerLists,
        IReadOnlyList<SmartCollection> smartCollections) => new(
        customShows
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new CustomShowSummary(
                c.Id,
                c.Name,
                c.ContentCount,
                c.ContentCount - c.SchedulableContentCount is > 0 and var n ? n : null,
                Math.Round(c.TotalDuration / TimeSpan.MillisecondsPerHour, 1)))
            .ToArray(),
        fillerLists
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FillerListSummary(f.Id, f.Name, f.ContentCount))
            .ToArray(),
        smartCollections
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SmartCollectionSummary(s.Uuid, s.Name, string.IsNullOrWhiteSpace(s.FilterString) ? null : s.FilterString))
            .ToArray());
}
