using System.Text.Json.Nodes;
using ModelContextProtocol;
using TunarrMcp.Server.Scheduling;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Tests.Unit.Scheduling;

public sealed class ScheduleBuilderTests
{
    private static readonly Channel[] Channels = [new("c5", 5, "Sam-O-Rama", null, 0, 0, 0, false, null)];

    private static readonly TimeProvider Chicago = new FixedClock(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(-6));

    private const int Hour = 3_600_000;

    private static JsonObject Build(ScheduleInput input, JsonObject? current = null) =>
        ScheduleBuilder.Build(input, current, Channels, Chicago);

    private static string Problems(ScheduleInput input, JsonObject? current = null) =>
        Assert.Throws<McpException>(() => Build(input, current)).Message;

    private static JsonObject Slot(JsonObject schedule, int i) => schedule["slots"]![i]!.AsObject();

    [Fact]
    public void New_time_schedule_gets_the_web_ui_defaults_and_this_servers_utc_offset()
    {
        var schedule = Build(new ScheduleInput("time", [new SlotInput("show", "s1", At: "07:00")]));

        Assert.Equal("time", (string?)schedule["type"]);
        Assert.Equal("day", (string?)schedule["period"]);
        Assert.Equal(1, (int?)schedule["padMs"]);
        Assert.Equal(365, (int?)schedule["maxDays"]);
        Assert.Equal(0, (int?)schedule["latenessMs"]);
        Assert.Equal("distribute", (string?)schedule["flexPreference"]);
        Assert.Equal(360, (int?)schedule["timeZoneOffset"]); // CST: 6 hours behind UTC, as JavaScript counts it
    }

    [Fact]
    public void Show_slot_starts_at_its_time_with_a_fresh_id_and_default_order()
    {
        var slot = Slot(Build(new ScheduleInput("time", [new SlotInput("show", "s1", At: "19:30", OnlySeasons: [1, 2])])), 0);

        Assert.Equal("show", (string?)slot["type"]);
        Assert.Equal("s1", (string?)slot["showId"]);
        Assert.Equal(19 * Hour + 30 * 60_000, (int?)slot["startTime"]);
        Assert.True(Guid.TryParse((string?)slot["id"], out _));
        Assert.Equal("next", (string?)slot["order"]);
        Assert.Equal("asc", (string?)slot["direction"]);
        Assert.Equal("[1,2]", slot["seasonFilter"]!.ToJsonString());
        Assert.Equal("[]", slot["seasonExcludeFilter"]!.ToJsonString());
    }

    [Theory]
    [InlineData("Sun 00:00", 0)]
    [InlineData("mon 18:00", 24 + 18)]
    [InlineData("Saturday 23:30", 6 * 24 + 23.5)]
    public void Weekly_slots_count_from_sunday_midnight(string at, double hours)
    {
        var slot = Slot(Build(new ScheduleInput("time", [new SlotInput("flex", At: at)], Period: "week")), 0);

        Assert.Equal((int)(hours * Hour), (int?)slot["startTime"]);
    }

    [Fact]
    public void Random_slot_defaults_to_one_program_weight_one_and_no_cooldown()
    {
        var schedule = Build(new ScheduleInput("random",
        [
            new SlotInput("custom_show", "cs1", Order: "shuffle"),
            new SlotInput("flex", Minutes: 30, Weight: 2),
        ]));

        Assert.Equal("uniform", (string?)schedule["randomDistribution"]);
        Assert.Equal("slot", (string?)schedule["padStyle"]);
        var movies = Slot(schedule, 0);
        Assert.Equal("custom-show", (string?)movies["type"]);
        Assert.Equal("cs1", (string?)movies["customShowId"]);
        Assert.Equal(1, (double?)movies["weight"]);
        Assert.Equal(0, (int?)movies["cooldownMs"]);
        Assert.Equal("""{"type":"dynamic","programCount":1}""", movies["durationSpec"]!.ToJsonString());
        Assert.Equal(0, (int?)movies["index"]);
        var flex = Slot(schedule, 1);
        Assert.Null(flex["id"]); // flex and redirect slots have no id in Tunarr's schema
        Assert.Equal("""{"type":"fixed","durationMs":1800000}""", flex["durationSpec"]!.ToJsonString());
        Assert.Equal(1, (int?)flex["index"]);
    }

    [Fact]
    public void Filler_slot_gets_the_web_ui_repeat_avoidance_defaults()
    {
        var slot = Slot(Build(new ScheduleInput("random", [new SlotInput("filler", "f1")])), 0);

        Assert.Equal("f1", (string?)slot["fillerListId"]);
        Assert.Equal("shuffle_prefer_short", (string?)slot["order"]);
        Assert.Equal("linear", (string?)slot["durationWeighting"]);
        Assert.Equal(0.5, (double?)slot["decayFactor"]);
        Assert.Equal(0.05, (double?)slot["recoveryFactor"]);
    }

    [Fact]
    public void Redirect_finds_the_channel_by_number()
    {
        var slot = Slot(Build(new ScheduleInput("time", [new SlotInput("redirect", "5", At: "02:00")])), 0);

        Assert.Equal("c5", (string?)slot["channelId"]);
        Assert.Equal("Sam-O-Rama", (string?)slot["channelName"]);
    }

    [Fact]
    public void Filler_around_a_slot_is_written_as_tunarr_expects()
    {
        var slot = Slot(Build(new ScheduleInput("random", [new SlotInput("show", "s1", Filler: [new FillerInput("f1", ["pre", "post"])])])), 0);

        Assert.Equal("""[{"types":["pre","post"],"fillerListId":"f1","fillerOrder":"shuffle_prefer_short"}]""", slot["filler"]!.ToJsonString());
    }

    [Fact]
    public void Settings_left_out_keep_the_current_schedules_values()
    {
        var current = JsonNode.Parse("""
            {"type":"random","flexPreference":"end","maxDays":7,"padMs":300000,"padStyle":"episode",
             "randomDistribution":"weighted","lockWeights":false,"periodMs":86400000,"slots":[]}
            """)!.AsObject();

        var schedule = Build(new ScheduleInput("random", [new SlotInput("show", "s1")], MaxDays: 14), current);

        Assert.Equal(14, (int?)schedule["maxDays"]);
        Assert.Equal(300000, (int?)schedule["padMs"]);
        Assert.Equal("end", (string?)schedule["flexPreference"]);
        Assert.Equal("weighted", (string?)schedule["randomDistribution"]);
        Assert.Equal(86400000, (int?)schedule["periodMs"]); // a field the input doesn't cover at all
    }

    [Fact]
    public void A_schedule_of_the_other_type_is_not_carried_over()
    {
        var current = JsonNode.Parse("""{"type":"random","maxDays":7,"padMs":300000,"slots":[]}""")!.AsObject();

        var schedule = Build(new ScheduleInput("time", [new SlotInput("flex", At: "00:00")]), current);

        Assert.Equal(365, (int?)schedule["maxDays"]);
        Assert.Equal(1, (int?)schedule["padMs"]);
    }

    [Fact]
    public void Slot_given_by_slotId_keeps_what_the_input_leaves_out_and_drops_display_fields()
    {
        var current = JsonNode.Parse("""
            {"type":"random","maxDays":7,"padMs":1,"slots":[
              {"id":"slot-1","type":"show","showId":"s1","order":"shuffle","direction":"asc","seasonFilter":[],"seasonExcludeFilter":[9],
               "weight":9.99,"cooldownMs":1800000,"durationSpec":{"type":"dynamic","programCount":1},"index":0,
               "linkMode":"continue","midRoll":{"intervalMs":600000,"maxBreaks":2,"minProgramDurationMs":0,"breakDurationMs":60000},
               "show":{"title":"Scrubs"},"isMissing":false}]}
            """)!.AsObject();

        var slot = Slot(Build(new ScheduleInput("random", [new SlotInput("show", SlotId: "slot-1", Weight: 30)]), current), 0);

        Assert.Equal("slot-1", (string?)slot["id"]);
        Assert.Equal(30, (double?)slot["weight"]);
        Assert.Equal("s1", (string?)slot["showId"]);
        Assert.Equal("shuffle", (string?)slot["order"]);
        Assert.Equal("[9]", slot["seasonExcludeFilter"]!.ToJsonString());
        Assert.Equal(1800000, (int?)slot["cooldownMs"]);
        Assert.NotNull(slot["midRoll"]);
        Assert.Equal("continue", (string?)slot["linkMode"]);
        Assert.Null(slot["show"]);
        Assert.Null(slot["isMissing"]);
    }

    [Fact]
    public void Slot_with_mid_roll_breaks_may_keep_mid_filler()
    {
        var current = JsonNode.Parse("""
            {"type":"random","slots":[{"id":"slot-1","type":"show","showId":"s1","midRoll":{"intervalMs":1,"maxBreaks":1,"minProgramDurationMs":0,"breakDurationMs":1}}]}
            """)!.AsObject();

        var slot = Slot(Build(new ScheduleInput("random", [new SlotInput("show", SlotId: "slot-1", Filler: [new FillerInput("f1", ["mid"])])]), current), 0);

        Assert.Contains("mid", slot["filler"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("when must be", Problems(new ScheduleInput("random", [new SlotInput("show", "s1", Filler: [new FillerInput("f1", ["mid"])])])),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_or_mismatched_slotId_is_a_problem()
    {
        var current = JsonNode.Parse("""{"type":"time","slots":[{"id":"slot-1","type":"show","showId":"s1","startTime":0}]}""")!.AsObject();

        Assert.Contains("no slot 'nope'", Problems(new ScheduleInput("time", [new SlotInput("show", SlotId: "nope", At: "07:00")]), current),
            StringComparison.Ordinal);
        Assert.Contains("plays show, not custom-show", Problems(new ScheduleInput("time", [new SlotInput("custom_show", SlotId: "slot-1")]), current),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Pad_of_zero_minutes_is_tunarrs_one_millisecond()
    {
        Assert.Equal(1, (int?)Build(new ScheduleInput("time", [new SlotInput("flex", At: "00:00")], PadMinutes: 0))["padMs"]);
        Assert.Equal(1_800_000, (int?)Build(new ScheduleInput("time", [new SlotInput("flex", At: "00:00")], PadMinutes: 30))["padMs"]);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var message = Problems(new ScheduleInput("time",
        [
            new SlotInput("show", At: "7pm"),
            new SlotInput("movie", "x", At: "08:00"),
            new SlotInput("flex", At: "Mon 09:00"),
            new SlotInput("flex", At: "23:00", Weight: 2),
            new SlotInput("flex", At: "23:00"),
            new SlotInput("custom_show", "cs1", At: "10:00", OnlySeasons: [1]),
        ], Period: "fortnight"));

        Assert.StartsWith("The schedule has problems, nothing was sent to Tunarr:", message, StringComparison.Ordinal);
        Assert.Contains("period must be day, week", message, StringComparison.Ordinal);
        Assert.Contains("slot 0: show slots need an id", message, StringComparison.Ordinal);
        Assert.Contains("slot 0: at must look like 19:30", message, StringComparison.Ordinal);
        Assert.Contains("slot 1: plays must be", message, StringComparison.Ordinal);
        Assert.Contains("slot 3: weight, programs, minutes and cooldownMinutes are for random slots", message, StringComparison.Ordinal);
        Assert.Contains("slot 4: starts at the same time as slot 3", message, StringComparison.Ordinal);
        Assert.Contains("slot 5: only show slots take season filters", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("day", "Mon 09:00", "names a day, but the schedule repeats daily")]
    [InlineData("week", "09:00", "weekly slots need a day")]
    [InlineData("day", "24:00", "is not a time of day")]
    public void Start_times_must_fit_the_period(string period, string at, string problem)
    {
        Assert.Contains(problem, Problems(new ScheduleInput("time", [new SlotInput("flex", At: at)], Period: period)), StringComparison.Ordinal);
    }

    [Fact]
    public void Random_slot_problems()
    {
        var message = Problems(new ScheduleInput("random",
        [
            new SlotInput("flex"),
            new SlotInput("redirect", "5", Programs: 2),
            new SlotInput("show", "s1", Programs: 2, Minutes: 30),
            new SlotInput("show", "s1", Weight: 0, At: "07:00"),
            new SlotInput("redirect", "42", Minutes: 10),
        ]));

        Assert.Contains("slot 0: flex slots need minutes", message, StringComparison.Ordinal);
        Assert.Contains("slot 1: redirect slots run for a number of minutes", message, StringComparison.Ordinal);
        Assert.Contains("slot 2: give programs or minutes, not both", message, StringComparison.Ordinal);
        Assert.Contains("slot 3: at is for time slots", message, StringComparison.Ordinal);
        Assert.Contains("slot 3: weight must be more than 0", message, StringComparison.Ordinal);
        Assert.Contains("slot 4: no channel '42' to redirect to", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_schedule_type_is_rejected()
    {
        Assert.Contains("type must be time or random", Problems(new ScheduleInput("weekly", [])), StringComparison.Ordinal);
    }
}

/// <summary>A clock stopped at <paramref name="now"/>, in a fixed-offset zone (e.g. -6 h for Chicago in winter).</summary>
internal sealed class FixedClock(DateTimeOffset now, TimeSpan offset) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Test", offset, "Test", "Test");
}
