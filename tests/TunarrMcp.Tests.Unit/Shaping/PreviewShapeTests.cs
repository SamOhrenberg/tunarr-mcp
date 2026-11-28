using TunarrMcp.Server.Shaping;
using TunarrMcp.Tests.Unit.Scheduling;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Tests.Unit.Shaping;

public sealed class PreviewShapeTests
{
    private static readonly Channel Channel = new("c99", 99, "MCP Sandbox", null, 0, 0, 0, false, null);
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    private const int Hour = 3_600_000;

    private static readonly Dictionary<string, LineupProgramEntry> Programs = new()
    {
        ["a"] = new(new ProgramDetails("a", "episode", "Magic Xylophone", null, 1, new SearchHitParent("s", "Bluey (2018)", null), new SearchHitParent("se", null, 1), "ok")),
        ["b"] = new(new ProgramDetails("b", "movie", "Castle in the Sky", 1986, null, null, null, "ok")),
    };

    // One hour of Bluey, one hour of flex, two hours of movie: a four-hour loop.
    private static readonly SchedulePreview Preview = new(Start.ToUnixTimeMilliseconds(),
    [
        new LineupItem("content", "a", Hour, null, null),
        new LineupItem("flex", null, Hour, null, null),
        new LineupItem("content", "b", 2 * Hour, null, null),
    ], Programs);

    private static SchedulePreviewView Shape(DateTimeOffset now, double hours) =>
        PreviewShape.From(Channel, "time_slots", Preview, TimeSpan.FromHours(hours), new FixedClock(now, TimeSpan.FromHours(-6)));

    [Fact]
    public void Starts_with_what_is_on_now_in_local_time_and_wraps_around_the_loop()
    {
        // 9.5 h after the start: 1.5 h into the third pass, so the flex hour is on (it began at 9:00 UTC).
        // The window runs to 13:30, so the fourth pass's flex hour (from 13:00) is in it too.
        var view = Shape(Start.AddHours(9.5), hours: 4);

        Assert.Equal(["flex", "movie", "episode", "flex"], view.Upcoming.Select(i => i.Type));
        Assert.Equal(Start.AddHours(9), view.Upcoming[0].StartsAt);
        Assert.Equal(TimeSpan.FromHours(-6), view.Upcoming[0].StartsAt.Offset);
        Assert.Equal("Bluey (2018)", view.Upcoming[2].Show);
        Assert.Equal(3, view.GeneratedItems);
        Assert.Equal(0.2, view.GeneratedDays);
    }

    [Fact]
    public void Flex_share_counts_only_the_previewed_window()
    {
        // From 9:30 for 2 h: 0.5 h left of the flex hour (counted in full from 9:00, since it's on), then movie.
        var view = Shape(Start.AddHours(9.5), hours: 2);

        Assert.Equal(["flex", "movie"], view.Upcoming.Select(i => i.Type));
        Assert.Equal(40, view.FlexPercent); // 1 h of flex out of 2.5 h shown
    }

    [Fact]
    public void A_channel_that_has_not_started_yet_previews_from_its_start()
    {
        var view = Shape(Start.AddHours(-1), hours: 1);

        Assert.Equal(Start, view.Upcoming[0].StartsAt);
        Assert.Equal("Magic Xylophone", view.Upcoming[0].Title);
    }

    [Fact]
    public void Empty_lineup_previews_nothing()
    {
        var view = PreviewShape.From(Channel, "random_slots", new SchedulePreview(0, [], null), TimeSpan.FromHours(1), TimeProvider.System);

        Assert.Empty(view.Upcoming);
        Assert.Equal(0, view.FlexPercent);
    }
}
