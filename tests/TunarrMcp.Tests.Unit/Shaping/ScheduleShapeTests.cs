using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Tests.Unit.Shaping;

public sealed class ScheduleShapeTests
{
    private static readonly Channel Channel = new("c5", 5, "Sam-O-Rama", null, 0, 0, 0, false, null);
    private static readonly Dictionary<string, string> Fillers = new() { ["f1"] = "Toonami Filler" };

    private const double Hour = 3_600_000;

    private static ChannelScheduleView Shape(ChannelSchedule schedule, bool? rebuild = null) =>
        ScheduleShape.From(Channel, new ChannelScheduleResponse(schedule, rebuild), Fillers);

    [Fact]
    public void Manual_channel_has_a_note_and_no_slots()
    {
        var view = ScheduleShape.From(Channel, null, Fillers);

        Assert.Equal("manual", view.Programming);
        Assert.Contains("get_channel_lineup", view.Note, StringComparison.Ordinal);
        Assert.Empty(view.Slots);
        Assert.Null(view.Settings);
    }

    [Fact]
    public void Time_slots_are_sorted_by_start_time_with_local_clock_times()
    {
        var view = Shape(new ChannelSchedule("time")
        {
            Period = "day",
            PadMs = 300_000,
            LatenessMs = 0,
            Slots =
            [
                new ScheduleSlot("flex") { StartTime = 20 * Hour },
                new ScheduleSlot("show") { StartTime = 7 * Hour + 30 * 60_000, ShowId = "s1", Show = new SlotTarget("Bluey (2018)", null, null), Order = "next" },
            ],
        });

        Assert.Equal("time_slots", view.Programming);
        Assert.Equal(["07:30", "20:00"], view.Slots.Select(s => s.At));
        Assert.Equal("Bluey (2018)", view.Slots[0].Title);
        Assert.Equal("flex", view.Slots[1].Plays);
        Assert.Null(view.Slots[0].Weight); // random-slot fields stay out of time slots
        Assert.Equal("day", view.Settings?.Period);
        Assert.Equal(5, view.Settings?.PadMinutes);
        Assert.Null(view.Settings?.Distribution);
    }

    [Theory]
    [InlineData(0, "Sun 00:00")]
    [InlineData(1 * 24 + 18, "Mon 18:00")]
    [InlineData(6 * 24 + 23.5, "Sat 23:30")]
    public void Weekly_slots_name_the_day_starting_sunday(double hours, string expected)
    {
        Assert.Equal(expected, ScheduleShape.At(hours * Hour, weekly: true));
    }

    [Fact]
    public void Random_slots_keep_their_order_and_show_weight_length_and_cooldown()
    {
        var view = Shape(new ChannelSchedule("random")
        {
            RandomDistribution = "weighted",
            PadStyle = "slot",
            Slots =
            [
                new ScheduleSlot("show")
                {
                    ShowId = "s2", Show = new SlotTarget("Scrubs", null, null), Weight = 9.99, CooldownMs = 1_800_000,
                    DurationSpec = new SlotDurationSpec("dynamic", null, 1), Order = "shuffle", SeasonExcludeFilter = [9],
                    Filler = [new SlotFiller(["pre"], "f1", "shuffle_prefer_short")],
                },
                new ScheduleSlot("custom-show")
                {
                    CustomShowId = "cs1", CustomShow = new SlotTarget(null, "TRCPD Movies", null), Weight = 1,
                    DurationSpec = new SlotDurationSpec("fixed", 1_800_000, null), Order = "chronological", Direction = "desc",
                },
            ],
        }, rebuild: false);

        Assert.Equal(["s2", "cs1"], view.Slots.Select(s => s.Id));
        var scrubs = view.Slots[0];
        Assert.Null(scrubs.At);
        Assert.Equal(9.99, scrubs.Weight);
        Assert.Equal("1 program", scrubs.Length);
        Assert.Equal(30, scrubs.CooldownMinutes);
        Assert.Equal([9], scrubs.ExceptSeasons!);
        Assert.Null(scrubs.OnlySeasons);
        Assert.Equal(["Toonami Filler: pre"], scrubs.Filler!);
        var movies = view.Slots[1];
        Assert.Equal("custom_show", movies.Plays);
        Assert.Equal("TRCPD Movies", movies.Title);
        Assert.Equal("30 min", movies.Length);
        Assert.Equal("chronological (descending)", movies.Order);
        Assert.Equal("weighted", view.Settings?.Distribution);
        Assert.Null(view.Settings?.Period);
        Assert.False(view.Settings?.RebuildEachCycle);
    }

    [Theory]
    [InlineData("dynamic", null, 3, "3 programs")]
    [InlineData("fixed", 5_400_000d, null, "90 min")]
    public void Length_describes_the_duration_spec(string type, double? ms, int? count, string expected)
    {
        Assert.Equal(expected, ScheduleShape.Length(new SlotDurationSpec(type, ms, count)));
    }

    [Fact]
    public void Slot_whose_show_is_gone_is_marked_missing()
    {
        var view = Shape(new ChannelSchedule("random")
        {
            Slots =
            [
                new ScheduleSlot("show") { ShowId = "gone", MissingShow = new SlotTarget("Old Show", null, null) },
                new ScheduleSlot("show") { ShowId = "ok", Show = new SlotTarget("Bluey (2018)", null, null) },
            ],
        });

        Assert.True(view.Slots[0].Missing);
        Assert.Equal("Old Show", view.Slots[0].Title);
        Assert.Null(view.Slots[1].Missing);
    }

    [Fact]
    public void Filler_list_unknown_to_the_server_falls_back_to_its_id()
    {
        var view = Shape(new ChannelSchedule("random")
        {
            Slots = [new ScheduleSlot("filler") { FillerListId = "f9", Order = "uniform" }],
        });

        Assert.Equal("f9", view.Slots[0].Title);
    }
}
