using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Shaping;

/// <param name="NextPage">Pass as page to get more; null on the last page.</param>
public sealed record LibrarySearchResults(int TotalHits, int Page, int? NextPage, IReadOnlyList<LibraryItem> Results);

/// <summary>
/// A show, movie or episode. Seasons and Episodes are set for shows; Minutes for movies and episodes;
/// Show, Season and Episode number for episodes.
/// </summary>
/// <param name="Source">The media server it comes from. The same title can exist once per source.</param>
public sealed record LibraryItem(
    string Id,
    string Type,
    string Title,
    int? Year,
    string? Source,
    int? Seasons,
    int? Episodes,
    double? Minutes,
    IReadOnlyList<string>? Genres,
    string? Show,
    int? SeasonNumber,
    int? EpisodeNumber);

/// <param name="Seasons">In season order. Season 0 is usually specials.</param>
public sealed record ShowDetail(
    string Id,
    string Title,
    int? Year,
    string? Source,
    string? Summary,
    IReadOnlyList<string>? Genres,
    int? TotalEpisodes,
    IReadOnlyList<SeasonSummary> Seasons,
    SeasonEpisodes? Season);

public sealed record SeasonSummary(int? Number, string Id, int? Episodes);

/// <summary>The episodes of the season that was asked for, in episode order.</summary>
public sealed record SeasonEpisodes(int? Number, string Id, IReadOnlyList<EpisodeSummary> Episodes);

/// <param name="Missing">True when the media server no longer has the file, so it can't be scheduled; otherwise left out.</param>
public sealed record EpisodeSummary(string Id, int? Number, string Title, double Minutes, string? Summary, bool? Missing);

internal static class LibraryShape
{
    private const int MaxGenres = 4;
    private const int MaxSummaryLength = 160;

    public static LibrarySearchResults From(ProgramSearchResult result, int page, IReadOnlyDictionary<string, string> sourceNames)
    {
        var lastPage = Math.Max(1, result.TotalPages);
        return new LibrarySearchResults(
            result.TotalHits,
            page,
            page < lastPage ? page + 1 : null,
            result.Results.Select(hit => Item(hit, sourceNames)).ToArray());
    }

    private static LibraryItem Item(SearchHit hit, IReadOnlyDictionary<string, string> sourceNames)
    {
        var isShow = hit.Type == "show";
        var isEpisode = hit.Type == "episode";
        return new LibraryItem(
            hit.Uuid,
            hit.Type,
            hit.Title,
            hit.Year,
            SourceName(hit.MediaSourceId, sourceNames),
            Seasons: isShow ? hit.ChildCount : null,
            Episodes: isShow ? hit.GrandchildCount : hit.Type == "season" ? hit.ChildCount : null,
            Minutes: hit.Duration is { } ms ? Minutes(ms) : null,
            Genres(hit.Genres),
            Show: isEpisode ? hit.Show?.Title : null,
            SeasonNumber: isEpisode ? hit.Season?.Index : null,
            EpisodeNumber: isEpisode ? hit.EpisodeNumber : null);
    }

    public static ShowDetail From(Show show, SeasonChildren? episodes, Season? season, IReadOnlyDictionary<string, string> sourceNames) => new(
        show.Uuid,
        show.Title,
        show.Year,
        SourceName(show.MediaSourceId, sourceNames),
        Truncate(show.Summary),
        Genres(show.Genres),
        show.GrandchildCount,
        (show.Seasons ?? [])
            .OrderBy(s => s.Index ?? int.MaxValue)
            .Select(s => new SeasonSummary(s.Index, s.Uuid, s.ChildCount))
            .ToArray(),
        season is null || episodes is null
            ? null
            : new SeasonEpisodes(season.Index, season.Uuid, episodes.Result.Programs
                .OrderBy(e => e.EpisodeNumber ?? int.MaxValue)
                .Select(e => new EpisodeSummary(e.Uuid, e.EpisodeNumber, e.Title, Minutes(e.Duration), Truncate(e.Summary),
                    Missing: e.State is not null and not "ok" ? true : null))
                .ToArray()));

    private static string? SourceName(string? id, IReadOnlyDictionary<string, string> names) =>
        id is null ? null : names.GetValueOrDefault(id, id);

    private static double Minutes(double ms) => Math.Round(ms / TimeSpan.MillisecondsPerMinute, 1);

    private static string[]? Genres(IReadOnlyList<Genre>? genres) =>
        genres is null || genres.Count == 0 ? null : genres.Take(MaxGenres).Select(g => g.Name).ToArray();

    internal static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        return text.Length <= MaxSummaryLength ? text : text[..MaxSummaryLength].TrimEnd() + "…";
    }
}
