using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Tests.Unit.Shaping;

public sealed class LineupShapeTests
{
    private static readonly Channel Channel = new("c3", 3, "Animation Station", null, 0, 0, 0, false, null);

    private static ChannelLineup Lineup(int total, IReadOnlyList<LineupItem> items, Dictionary<string, LineupProgramEntry>? programs = null,
        IReadOnlyList<double>? offsets = null) =>
        new("Animation Station", 3, total, items, programs, offsets);

    [Fact]
    public void Episodes_name_their_show_season_and_number()
    {
        var program = new LineupProgram("p1", "episode", "Welcome to Republic City", 2012, 1,
            new SearchHitParent("s1", "The Legend of Korra", null), new SearchHitParent("se1", "Season 1", 1), "ok");
        var lineup = Lineup(183, [new LineupItem("content", "p1", 1_444_075, null, null)],
            new() { ["p1"] = new LineupProgramEntry(program) }, offsets: [5_400_000]);

        var view = LineupShape.From(Channel, "manual", lineup, offset: 4);

        var item = Assert.Single(view.Items);
        Assert.Equal(4, item.Position);
        Assert.Equal(1.5, item.StartsAtHour);
        Assert.Equal("episode", item.Type);
        Assert.Equal("The Legend of Korra", item.Show);
        Assert.Equal(1, item.SeasonNumber);
        Assert.Equal(1, item.EpisodeNumber);
        Assert.Equal(24.1, item.Minutes);
        Assert.Null(item.Missing);
        Assert.Equal(5, view.NextOffset);
    }

    [Fact]
    public void Movies_leave_episode_fields_out_and_missing_files_are_flagged()
    {
        var program = new LineupProgram("m1", "movie", "Castle in the Sky", 1986, null, null, null, "missing");
        var lineup = Lineup(1, [new LineupItem("content", "m1", 7_471_260, null, null)], new() { ["m1"] = new LineupProgramEntry(program) });

        var item = Assert.Single(LineupShape.From(Channel, "manual", lineup, 0).Items);

        Assert.Null(item.Show);
        Assert.Null(item.EpisodeNumber);
        Assert.True(item.Missing);
    }

    [Fact]
    public void Flex_and_redirect_items_need_no_program()
    {
        var lineup = Lineup(2,
        [
            new LineupItem("flex", null, 600_000, null, null),
            new LineupItem("redirect", null, 1_800_000, "Sam-O-Rama", 5),
        ]);

        var view = LineupShape.From(Channel, "manual", lineup, 0);

        Assert.Equal(["flex", "redirect"], view.Items.Select(i => i.Type));
        Assert.Equal("5 Sam-O-Rama", view.Items[1].Title);
        Assert.Null(view.NextOffset); // last page
    }

    [Fact]
    public void Content_without_details_keeps_its_id()
    {
        var item = Assert.Single(LineupShape.From(Channel, "manual", Lineup(1, [new LineupItem("custom", "p9", 60_000, null, null)]), 0).Items);

        Assert.Equal("custom", item.Type);
        Assert.Equal("p9", item.Id);
        Assert.Null(item.Title);
    }

    [Fact]
    public void Offset_past_the_end_gives_an_empty_last_page()
    {
        var view = LineupShape.From(Channel, "manual", Lineup(183, []), 500);

        Assert.Empty(view.Items);
        Assert.Null(view.NextOffset);
    }
}
