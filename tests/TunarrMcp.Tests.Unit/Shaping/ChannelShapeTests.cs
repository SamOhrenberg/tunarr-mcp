using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Tests.Unit.Shaping;

public sealed class ChannelShapeTests
{
    private static Channel Channel(string id, int number, string? group = null, bool stealth = false, bool? onDemand = null) =>
        new(id, number, $"Channel {number}", group, Duration: 5_400_000, ProgramCount: 3, StartTime: 0, stealth,
            onDemand is null ? null : new ChannelOnDemand(onDemand.Value));

    [Fact]
    public void Channels_are_sorted_by_number_and_say_how_they_are_programmed()
    {
        var channels = new[] { Channel("c", 3), Channel("a", 1), Channel("b", 2) };
        var schedules = new Dictionary<string, ChannelScheduleResponse?>
        {
            ["a"] = new(new ChannelSchedule("time"), false),
            ["b"] = new(new ChannelSchedule("random"), null),
            ["c"] = null,
        };

        var list = ChannelShape.From(channels, schedules);

        Assert.Equal([1, 2, 3], list.Channels.Select(c => c.Number));
        Assert.Equal(["time_slots", "random_slots", "manual"], list.Channels.Select(c => c.Programming));
    }

    [Fact]
    public void A_schedule_type_this_server_does_not_know_is_passed_through()
    {
        Assert.Equal("someday", ChannelShape.Programming(new(new ChannelSchedule("someday"), null)));
    }

    [Fact]
    public void Lineup_length_is_in_hours_and_blank_group_is_null()
    {
        var summary = Assert.Single(ChannelShape.From([Channel("a", 1, group: " ")], new Dictionary<string, ChannelScheduleResponse?>()).Channels);

        Assert.Equal(1.5, summary.LineupHours);
        Assert.Null(summary.Group);
        Assert.Equal("manual", summary.Programming); // no schedule fetched counts as none
    }

    [Fact]
    public void Flags_default_to_false()
    {
        var summary = Assert.Single(ChannelShape.From([Channel("a", 1, stealth: true)], new Dictionary<string, ChannelScheduleResponse?>()).Channels);

        Assert.True(summary.HiddenFromGuide);
        Assert.False(summary.OnDemand);
    }
}
