using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Tools;

[McpServerToolType]
public sealed class LibraryTools(TunarrClient tunarr)
{
    [McpServerTool(Name = "search_library", Title = "Search the library", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Find shows, movies or episodes in the media libraries Tunarr knows about, by title words, genre or both.
        Returns ids for get_show and the channel tools, 20 per page. The same title can appear once per media server
        (source); stick to one source per channel unless asked otherwise. Episode searches match episode titles only:
        to pick episodes of a known show, use get_show with a season instead.
        """)]
    public async Task<LibrarySearchResults> SearchLibrary(
        [Description("Words from the title. Leave out to browse by type and genre.")] string? query = null,
        [Description("show, movie, episode, or show_or_movie.")] string type = "show_or_movie",
        [Description("Exact genre name, e.g. Animation, Comedy, Family.")] string? genre = null,
        [Description("Media server name or id, to search only one source.")] string? source = null,
        [Description("1-based page number.")] int page = 1,
        CancellationToken cancellationToken = default)
    {
        string[] types = type switch
        {
            "show" or "movie" or "episode" => [type],
            "show_or_movie" => ["show", "movie"],
            _ => throw new McpException($"Unknown type '{type}'. Use show, movie, episode or show_or_movie."),
        };
        if (page < 1)
            throw new McpException("page starts at 1.");

        var sources = await tunarr.GetMediaSourcesAsync(cancellationToken).ConfigureAwait(false);
        var sourceId = source is null ? null : ResolveSource(source, sources);

        var result = await tunarr.SearchProgramsAsync(new LibrarySearch(query, types, genre, sourceId, page), cancellationToken)
            .ConfigureAwait(false);

        return LibraryShape.From(result, page, SourceNames(sources));
    }

    [McpServerTool(Name = "get_show", Title = "Get a show", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        A show's seasons with their episode counts. Pass season to also list that season's episodes with their ids,
        titles, runtimes and short summaries, for picking specific episodes.
        """)]
    public async Task<ShowDetail> GetShow(
        [Description("Show id from search_library.")] string showId,
        [Description("Season number to list episodes for. 0 is usually specials.")] int? season = null,
        CancellationToken cancellationToken = default)
    {
        var showTask = tunarr.GetShowAsync(showId, cancellationToken);
        var sourcesTask = tunarr.GetMediaSourcesAsync(cancellationToken);
        await Task.WhenAll(showTask, sourcesTask).ConfigureAwait(false);

        var show = await showTask.ConfigureAwait(false);
        if (show is null || show.Type != "show")
            throw new McpException($"No show has id '{showId}'. Use search_library with type show to find show ids.");

        Season? match = null;
        SeasonChildren? episodes = null;
        if (season is { } number)
        {
            match = show.Seasons?.FirstOrDefault(s => s.Index == number)
                ?? throw new McpException($"{show.Title} has no season {number}. Seasons: " +
                    string.Join(", ", (show.Seasons ?? []).Select(s => s.Index).OfType<int>().Order()) + ".");
            episodes = await tunarr.GetSeasonEpisodesAsync(match.Uuid, cancellationToken).ConfigureAwait(false);
        }

        return LibraryShape.From(show, episodes, match, SourceNames(await sourcesTask.ConfigureAwait(false)));
    }

    [McpServerTool(Name = "list_collections", Title = "List collections", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Tunarr's collections, with ids for schedule slots: custom shows (hand-picked, ordered lists of programs; the
        way to put movies in a slot), filler lists (bumpers and commercials played between programs or in gaps) and
        smart collections (saved library searches).
        """)]
    public async Task<CollectionList> ListCollections(CancellationToken cancellationToken)
    {
        var customShows = tunarr.GetCustomShowsAsync(cancellationToken);
        var fillerLists = tunarr.GetFillerListsAsync(cancellationToken);
        var smartCollections = tunarr.GetSmartCollectionsAsync(cancellationToken);
        await Task.WhenAll(customShows, fillerLists, smartCollections).ConfigureAwait(false);

        return CollectionShape.From(await customShows.ConfigureAwait(false), await fillerLists.ConfigureAwait(false),
            await smartCollections.ConfigureAwait(false));
    }

    private static string ResolveSource(string source, IReadOnlyList<MediaSource> sources) =>
        sources.FirstOrDefault(s => s.Id == source || string.Equals(s.Name, source, StringComparison.OrdinalIgnoreCase))?.Id
        ?? throw new McpException($"No media source named '{source}'. Sources: {string.Join(", ", sources.Select(s => s.Name))}.");

    private static Dictionary<string, string> SourceNames(IReadOnlyList<MediaSource> sources) =>
        sources.ToDictionary(s => s.Id, s => s.Name);
}
