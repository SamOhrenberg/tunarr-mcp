using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Tests.Unit.Shaping;

public sealed class LibraryShapeTests
{
    private static readonly Dictionary<string, string> Sources = new() { ["src1"] = "Jellyfin" };

    private static SearchHit Hit(string type, double? duration = null, int? children = null, int? grandchildren = null,
        string? source = "src1", IReadOnlyList<Genre>? genres = null) =>
        new("id", type, "Title", 2018, source, duration, children, grandchildren, EpisodeNumber: 26, genres,
            new SearchHitParent("s1", "Bluey (2018)", null), new SearchHitParent("se1", "Season 1", 1));

    private static ProgramSearchResult Result(params SearchHit[] hits) => new(hits, Page: 1, TotalPages: 1, TotalHits: hits.Length);

    [Fact]
    public void Show_counts_seasons_and_episodes()
    {
        var item = Assert.Single(LibraryShape.From(Result(Hit("show", children: 3, grandchildren: 154)), 1, Sources).Results);

        Assert.Equal(3, item.Seasons);
        Assert.Equal(154, item.Episodes);
        Assert.Null(item.Minutes);
        Assert.Null(item.Show); // parents only matter for episodes
        Assert.Equal("Jellyfin", item.Source);
    }

    [Fact]
    public void Episode_has_minutes_and_where_it_belongs()
    {
        var item = Assert.Single(LibraryShape.From(Result(Hit("episode", duration: 438_058)), 1, Sources).Results);

        Assert.Equal(7.3, item.Minutes);
        Assert.Equal("Bluey (2018)", item.Show);
        Assert.Equal(1, item.SeasonNumber);
        Assert.Equal(26, item.EpisodeNumber);
        Assert.Null(item.Seasons);
    }

    [Fact]
    public void Unknown_source_falls_back_to_its_id_and_genres_are_capped()
    {
        var genres = new[] { "A", "B", "C", "D", "E" }.Select(g => new Genre(g)).ToArray();

        var item = Assert.Single(LibraryShape.From(Result(Hit("movie", source: "src9", genres: genres)), 1, Sources).Results);

        Assert.Equal("src9", item.Source);
        Assert.Equal(["A", "B", "C", "D"], item.Genres!);
    }

    [Theory]
    [InlineData(1, 3, 2)]
    [InlineData(3, 3, null)]
    [InlineData(1, 0, null)] // no hits: Tunarr says 0 pages
    public void Next_page_is_null_on_the_last_page(int page, int totalPages, int? next)
    {
        var results = LibraryShape.From(new ProgramSearchResult([], page, totalPages, 0), page, Sources);

        Assert.Equal(next, results.NextPage);
    }

    [Fact]
    public void Show_lists_seasons_in_order_and_episodes_of_the_one_asked_for()
    {
        var seasons = new[] { new Season("se2", 2, "Season 2", 52), new Season("se0", 0, "Specials", 4), new Season("se1", 1, "Season 1", 52) };
        var show = new Show("s1", "show", "Bluey (2018)", 2018, "  A dog.  ", "src1", 3, 108, [], seasons);
        var episodes = new SeasonChildren(2, new SeasonChildrenResult("episode",
        [
            new Episode("e2", "Hammerbarn", 2, 438_016, null, "ok"),
            new Episode("e1", "Dance Mode", 1, 438_016, "", "missing"),
        ]));

        var detail = LibraryShape.From(show, episodes, seasons[0], Sources);

        Assert.Equal([0, 1, 2], detail.Seasons.Select(s => s.Number));
        Assert.Equal("A dog.", detail.Summary);
        Assert.Null(detail.Genres);
        Assert.Equal(2, detail.Season?.Number);
        Assert.Equal(["e1", "e2"], detail.Season!.Episodes.Select(e => e.Id));
        Assert.True(detail.Season.Episodes[0].Missing);
        Assert.Null(detail.Season.Episodes[1].Missing); // left out of the JSON unless true
        Assert.Null(detail.Season.Episodes[0].Summary);
    }

    [Fact]
    public void Long_summaries_are_cut_with_an_ellipsis()
    {
        var text = LibraryShape.Truncate(new string('x', 500));

        Assert.Equal(161, text!.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }
}
