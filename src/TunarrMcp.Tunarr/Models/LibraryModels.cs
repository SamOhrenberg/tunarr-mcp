using System.Text.Json.Serialization;

namespace TunarrMcp.Tunarr.Models;

/// <summary>One entry of GET /media-sources: a Plex, Jellyfin, Emby or local server and its libraries.</summary>
public sealed record MediaSource(string Id, string Name, string Type, IReadOnlyList<MediaLibrary> Libraries);

/// <summary>MediaType is movies, shows, music_videos, other_videos or tracks.</summary>
public sealed record MediaLibrary(string Id, string Name, string MediaType, bool Enabled);

/// <summary>What a library search asks for. The client turns this into Tunarr's search request body.</summary>
/// <param name="Query">Words to match in titles. Empty browses everything that matches the filters.</param>
/// <param name="Types">Program types to return: show, movie, episode, season, other_video, music_video, track.</param>
/// <param name="Genre">An exact genre name, e.g. "Animation".</param>
/// <param name="Page">1-based.</param>
public sealed record LibrarySearch(string? Query, IReadOnlyList<string> Types, string? Genre, string? MediaSourceId, int Page);

/// <summary>POST /programs/search. Page is 1-based for text queries and 0-based when browsing without one.</summary>
public sealed record ProgramSearchResult(IReadOnlyList<SearchHit> Results, int Page, int TotalPages, int TotalHits);

/// <summary>
/// One search result. Shows and seasons carry ChildCount (seasons or episodes) and GrandchildCount (a show's episodes);
/// movies and episodes carry Duration in ms. Show and Season are filled for episodes because the client asks Tunarr
/// to expand parents.
/// </summary>
public sealed record SearchHit(
    string Uuid,
    string Type,
    string Title,
    int? Year,
    string? MediaSourceId,
    double? Duration,
    int? ChildCount,
    int? GrandchildCount,
    int? EpisodeNumber,
    IReadOnlyList<Genre>? Genres,
    SearchHitParent? Show,
    SearchHitParent? Season);

public sealed record SearchHitParent(string? Uuid, string? Title, int? Index);

public sealed record Genre(string Name);

/// <summary>GET /program_groupings/:id for a show. Seasons are in no particular order.</summary>
public sealed record Show(
    string Uuid,
    string Type,
    string Title,
    int? Year,
    string? Summary,
    string? MediaSourceId,
    int? ChildCount,
    int? GrandchildCount,
    IReadOnlyList<Genre>? Genres,
    IReadOnlyList<Season>? Seasons);

/// <summary>Index is the season number; 0 is usually specials. ChildCount is its episode count.</summary>
public sealed record Season(string Uuid, int? Index, string? Title, int? ChildCount);

/// <summary>GET /programs/:id/children for a season.</summary>
public sealed record SeasonChildren(int Total, SeasonChildrenResult Result);

public sealed record SeasonChildrenResult(string Type, IReadOnlyList<Episode> Programs);

/// <summary>Duration is in ms. State is "ok", or e.g. "missing" when the media server no longer has the file.</summary>
public sealed record Episode(
    string Uuid,
    string Title,
    int? EpisodeNumber,
    double Duration,
    string? Summary,
    string? State);

// The request body Tunarr expects. Internal: callers use LibrarySearch.
internal sealed record ProgramSearchRequest(
    ProgramSearchQuery Query,
    string? MediaSourceId,
    int Page,
    int Limit,
    bool ExpandParents);

internal sealed record ProgramSearchQuery(
    string? Query,
    IReadOnlyList<string>? RestrictSearchTo,
    SearchFilter? Filter,
    IReadOnlyList<object>? Sort);

[JsonDerivedType(typeof(SearchFilterValue))]
[JsonDerivedType(typeof(SearchFilterOperator))]
internal abstract record SearchFilter(string Type);

internal sealed record SearchFilterValue(SearchFieldSpec FieldSpec) : SearchFilter("value");

internal sealed record SearchFilterOperator(string Op, IReadOnlyList<SearchFilter> Children) : SearchFilter("op");

internal sealed record SearchFieldSpec(string Key, string Type, string Op, IReadOnlyList<string> Value);
