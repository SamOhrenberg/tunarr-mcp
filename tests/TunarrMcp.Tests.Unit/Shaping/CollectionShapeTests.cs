using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Tests.Unit.Shaping;

public sealed class CollectionShapeTests
{
    [Fact]
    public void Custom_shows_are_sorted_with_hours_and_unschedulable_count_only_when_nonzero()
    {
        var list = CollectionShape.From(
            [
                new CustomShow("b", "TRCPD Movies", 9, 7, 67_703_816),
                new CustomShow("a", "animation Station Jr. Movies", 15, 15, 81_072_070),
            ],
            [], []);

        Assert.Equal(["a", "b"], list.CustomShows.Select(c => c.Id)); // case-insensitive name order
        Assert.Null(list.CustomShows[0].Unschedulable);
        Assert.Equal(2, list.CustomShows[1].Unschedulable);
        Assert.Equal(18.8, list.CustomShows[1].Hours);
    }

    [Fact]
    public void Unknown_schedulable_count_is_treated_as_all_schedulable()
    {
        var show = Assert.Single(CollectionShape.From([new CustomShow("a", "A", 3, null, 0)], [], []).CustomShows);

        Assert.Null(show.Unschedulable);
    }

    [Fact]
    public void Filler_lists_and_smart_collections_keep_their_ids()
    {
        var list = CollectionShape.From([],
            [new FillerList("f1", "Toonami Filler", 50)],
            [new SmartCollection("sc1", "90s Cartoons", "genre = \"Animation\" AND year < 2000", null), new SmartCollection("sc2", "Empty", " ", null)]);

        Assert.Equal(50, Assert.Single(list.FillerLists).Items);
        Assert.Equal("sc1", list.SmartCollections[0].Id);
        Assert.Contains("Animation", list.SmartCollections[0].Filter, StringComparison.Ordinal);
        Assert.Null(list.SmartCollections[1].Filter);
    }
}
