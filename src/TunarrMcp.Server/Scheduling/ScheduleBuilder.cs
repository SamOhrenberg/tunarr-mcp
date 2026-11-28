using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using TunarrMcp.Server.Tools;
using TunarrMcp.Tunarr;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Scheduling;

/// <summary>
/// Turns a <see cref="ScheduleInput"/> into the schedule JSON Tunarr saves. Starts from the channel's current
/// schedule when it has one of the same type, so settings and slot details the input doesn't mention survive;
/// otherwise from the defaults Tunarr's web UI uses. Every problem is reported at once, in one McpException.
/// </summary>
internal static partial class ScheduleBuilder
{
    private const double MinuteMs = 60_000;
    private const int DayMs = 86_400_000;

    private static readonly string[] Days = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    private static readonly string[] ContentOrders = ["next", "shuffle", "ordered_shuffle", "alphanumeric", "chronological"];
    private static readonly string[] FillerOrders = ["shuffle_prefer_short", "shuffle_prefer_long", "uniform"];
    private static readonly string[] FillerTimes = ["head", "pre", "post", "tail", "fallback"];

    public static JsonObject Build(ScheduleInput input, JsonObject? current, IReadOnlyList<Channel> channels, TimeProvider clock)
    {
        var problems = new List<string>();
        var isTime = input.Type switch
        {
            "time" => true,
            "random" => false,
            _ => Fail<bool>(problems, $"type must be time or random, not '{input.Type}'"),
        };
        if (problems.Count > 0)
            throw Problems(problems);

        // Same type: carry the current settings and slots over. Different type: start from the defaults.
        var sameType = current is not null && (string?)current["type"] == input.Type;
        var schedule = sameType ? (JsonObject)current!.DeepClone() : Defaults(isTime);
        var currentSlots = sameType && current!["slots"] is JsonArray slots
            ? slots.OfType<JsonObject>().Where(s => s["id"] is not null).ToDictionary(s => (string)s["id"]!, StringComparer.OrdinalIgnoreCase)
            : [];

        if (isTime)
        {
            if (input.Period is not null)
                schedule["period"] = OneOf(problems, "period", input.Period, ["day", "week"]);
            if (input.LatenessMinutes is { } lateness)
                schedule["latenessMs"] = lateness < 0 ? Fail<int>(problems, "latenessMinutes can't be negative") : WholeMs(lateness);
        }
        else
        {
            if (input.Distribution is not null)
                schedule["randomDistribution"] = OneOf(problems, "distribution", input.Distribution, ["weighted", "uniform", "none"]);
            if (input.PadStyle is not null)
                schedule["padStyle"] = OneOf(problems, "padStyle", input.PadStyle, ["slot", "episode"]);
        }

        if (input.PadMinutes is { } pad)
            schedule["padMs"] = pad < 0 ? Fail<int>(problems, "padMinutes can't be negative") : Math.Max(1, WholeMs(pad)); // Tunarr needs > 0; 1 ms is "none"
        if (input.FlexPreference is not null)
            schedule["flexPreference"] = OneOf(problems, "flexPreference", input.FlexPreference, ["distribute", "end"]);
        if (input.MaxDays is { } days)
            schedule["maxDays"] = days < 1 ? Fail<int>(problems, "maxDays must be at least 1") : days;

        // Tunarr places slots by its own clock; this offset (minutes behind UTC, as in JavaScript) is what its UI shows.
        schedule["timeZoneOffset"] = -(int)clock.LocalTimeZone.GetUtcOffset(clock.GetUtcNow()).TotalMinutes;

        var weekly = isTime && (string?)schedule["period"] == "week";
        if (input.Slots.Count == 0)
            problems.Add("give at least one slot");

        var built = new JsonArray();
        var starts = new Dictionary<int, int>();
        for (var i = 0; i < input.Slots.Count; i++)
        {
            var slotProblems = new List<string>();
            var slot = Slot(input.Slots[i], i, isTime, weekly, currentSlots, channels, slotProblems);
            if (isTime && slot?["startTime"] is JsonValue start)
            {
                var ms = start.GetValue<int>();
                if (starts.TryGetValue(ms, out var other))
                    slotProblems.Add($"starts at the same time as slot {other}");
                starts.TryAdd(ms, i);
            }

            problems.AddRange(slotProblems.Select(p => $"slot {i}: {p}"));
            if (slot is not null)
                built.Add(slot);
        }

        if (problems.Count > 0)
            throw Problems(problems);

        schedule["slots"] = built;
        return schedule;
    }

    private static JsonObject? Slot(SlotInput input, int index, bool isTime, bool weekly,
        Dictionary<string, JsonObject> current, IReadOnlyList<Channel> channels, List<string> problems)
    {
        var type = input.Plays switch
        {
            "show" => "show",
            "custom_show" => "custom-show",
            "smart_collection" => "smart-collection",
            "filler" => "filler",
            "flex" => "flex",
            "redirect" => "redirect",
            _ => null,
        };
        if (type is null)
        {
            problems.Add($"plays must be show, custom_show, smart_collection, filler, flex or redirect, not '{input.Plays}'");
            return null;
        }

        JsonObject slot;
        if (input.SlotId is { } slotId)
        {
            if (!current.TryGetValue(slotId, out var existing))
            {
                problems.Add($"no slot '{slotId}' in the channel's current {(isTime ? "time" : "random")}-slot schedule; leave slotId out for a new slot");
                return null;
            }

            if ((string?)existing["type"] != type)
            {
                problems.Add($"slot '{slotId}' plays {existing["type"]}, not {type}; leave slotId out to replace it with a new slot");
                return null;
            }

            slot = TunarrClient.StripSlotDisplayFields(existing);
        }
        else
        {
            slot = new JsonObject { ["type"] = type };
        }

        var isNew = input.SlotId is null;
        var playsContent = type is "show" or "custom-show" or "smart-collection";

        // What the slot plays.
        if (type == "flex")
        {
            if (input.Id is not null)
                problems.Add("flex slots don't take an id");
        }
        else if (input.Id is { } id)
        {
            switch (type)
            {
                case "show": slot["showId"] = id; break;
                case "custom-show": slot["customShowId"] = id; break;
                case "smart-collection": slot["smartCollectionId"] = id; break;
                case "filler": slot["fillerListId"] = id; break;
                case "redirect":
                    var channel = ChannelLookup.TryFind(channels, id);
                    if (channel is null)
                    {
                        problems.Add($"no channel '{id}' to redirect to");
                    }
                    else
                    {
                        slot["channelId"] = channel.Id;
                        slot["channelName"] = channel.Name;
                    }

                    break;
            }
        }
        else if (isNew)
        {
            problems.Add($"{input.Plays} slots need an id");
        }

        // Ids for slots that iterate through content. Flex and redirect slots have none.
        if (isNew && type is not ("flex" or "redirect"))
            slot["id"] = Guid.NewGuid().ToString();

        // Order.
        if (playsContent)
        {
            if (input.Order is not null)
                slot["order"] = OneOf(problems, "order", input.Order, ContentOrders);
            else if (isNew)
                slot["order"] = "next";
            if (input.Descending is { } descending)
                slot["direction"] = descending ? "desc" : "asc";
            else if (isNew)
                slot["direction"] = "asc";
        }
        else if (type == "filler")
        {
            if (input.Order is not null)
                slot["order"] = OneOf(problems, "order", input.Order, FillerOrders);
            else if (isNew)
                slot["order"] = "shuffle_prefer_short";
            if (isNew)
            {
                // The web UI's defaults for how filler picks avoid repeats.
                slot["durationWeighting"] = "linear";
                slot["decayFactor"] = 0.5;
                slot["recoveryFactor"] = 0.05;
            }
        }
        else if (input.Order is not null || input.Descending is not null)
        {
            problems.Add($"{input.Plays} slots have no order");
        }

        // Seasons.
        if (type == "show")
        {
            if (input.OnlySeasons is { } only)
                slot["seasonFilter"] = new JsonArray(only.Select(s => (JsonNode)s).ToArray());
            else if (isNew)
                slot["seasonFilter"] = new JsonArray();
            if (input.ExceptSeasons is { } except)
                slot["seasonExcludeFilter"] = new JsonArray(except.Select(s => (JsonNode)s).ToArray());
            else if (isNew)
                slot["seasonExcludeFilter"] = new JsonArray();
        }
        else if (input.OnlySeasons is not null || input.ExceptSeasons is not null)
        {
            problems.Add("only show slots take season filters");
        }

        // Filler around the slot's programs.
        if (input.Filler is { } filler)
        {
            if (!playsContent)
            {
                problems.Add("only show, custom_show and smart_collection slots take filler");
            }
            else
            {
                var hasMidRoll = slot["midRoll"] is not null;
                slot["filler"] = new JsonArray(filler.Select(f =>
                {
                    foreach (var when in f.When.Where(w => !FillerTimes.Contains(w) && !(w == "mid" && hasMidRoll)))
                        problems.Add($"filler '{f.FillerListId}': when must be {string.Join(", ", FillerTimes)}, not '{when}'");
                    if (f.When.Count == 0)
                        problems.Add($"filler '{f.FillerListId}': say when it plays");
                    return (JsonNode)new JsonObject
                    {
                        ["types"] = new JsonArray(f.When.Select(w => (JsonNode)w).ToArray()),
                        ["fillerListId"] = f.FillerListId,
                        ["fillerOrder"] = f.Order is null ? "shuffle_prefer_short" : OneOf(problems, "filler order", f.Order, FillerOrders),
                    };
                }).ToArray());
            }
        }

        if (isTime)
            TimeFields(input, slot, weekly, problems);
        else
            RandomFields(input, slot, index, isNew, type, problems);

        return slot;
    }

    private static void TimeFields(SlotInput input, JsonObject slot, bool weekly, List<string> problems)
    {
        if (input.Weight is not null || input.Programs is not null || input.Minutes is not null || input.CooldownMinutes is not null)
            problems.Add("weight, programs, minutes and cooldownMinutes are for random slots; time slots run until the next slot starts");

        if (input.At is null)
        {
            if (slot["startTime"] is null)
                problems.Add("time slots need at, e.g. 19:30" + (weekly ? " with a day: Mon 19:30" : ""));
            return;
        }

        var match = AtPattern().Match(input.At.Trim());
        if (!match.Success)
        {
            problems.Add($"at must look like 19:30{(weekly ? " with a day, e.g. Mon 19:30" : "")}, not '{input.At}'");
            return;
        }

        var hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        if (hours > 23 || minutes > 59)
        {
            problems.Add($"'{input.At}' is not a time of day");
            return;
        }

        var day = 0;
        if (match.Groups["d"].Success)
        {
            if (!weekly)
            {
                problems.Add($"'{input.At}' names a day, but the schedule repeats daily; set period to week for per-day slots");
                return;
            }

            day = Array.FindIndex(Days, d => match.Groups["d"].Value.StartsWith(d, StringComparison.OrdinalIgnoreCase));
        }
        else if (weekly)
        {
            problems.Add($"weekly slots need a day, e.g. Mon {input.At}");
            return;
        }

        slot["startTime"] = (day * DayMs) + (hours * 3_600_000) + (minutes * 60_000);
    }

    private static void RandomFields(SlotInput input, JsonObject slot, int index, bool isNew, string type, List<string> problems)
    {
        if (input.At is not null)
            problems.Add("at is for time slots; random slots have no start time");

        slot["index"] = index;
        if (input.Weight is { } weight)
            slot["weight"] = weight <= 0 ? Fail<double>(problems, "weight must be more than 0") : weight;
        else if (isNew)
            slot["weight"] = 1.0;
        if (input.CooldownMinutes is { } cooldown)
            slot["cooldownMs"] = cooldown < 0 ? Fail<int>(problems, "cooldownMinutes can't be negative") : WholeMs(cooldown);
        else if (isNew)
            slot["cooldownMs"] = 0;

        if (input.Programs is not null && input.Minutes is not null)
        {
            problems.Add("give programs or minutes, not both");
        }
        else if (input.Minutes is { } minutes)
        {
            slot["durationSpec"] = minutes <= 0
                ? Fail<JsonObject>(problems, "minutes must be more than 0")
                : new JsonObject { ["type"] = "fixed", ["durationMs"] = WholeMs(minutes) };
        }
        else if (input.Programs is { } programs)
        {
            if (type is "flex" or "redirect")
                problems.Add($"{input.Plays} slots run for a number of minutes, not programs");
            slot["durationSpec"] = programs < 1
                ? Fail<JsonObject>(problems, "programs must be at least 1")
                : new JsonObject { ["type"] = "dynamic", ["programCount"] = programs };
        }
        else if (slot["durationSpec"] is null)
        {
            if (type is "flex" or "redirect")
                problems.Add($"{input.Plays} slots need minutes");
            else
                slot["durationSpec"] = new JsonObject { ["type"] = "dynamic", ["programCount"] = 1 };
        }
    }

    private static JsonObject Defaults(bool isTime) => isTime
        ? new JsonObject
        {
            ["type"] = "time", ["flexPreference"] = "distribute", ["latenessMs"] = 0, ["maxDays"] = 365,
            ["overflow"] = new JsonObject { ["type"] = "duration", ["maxMs"] = 0 }, ["padMs"] = 1, ["period"] = "day",
        }
        : new JsonObject
        {
            ["type"] = "random", ["flexPreference"] = "distribute", ["maxDays"] = 365, ["padMs"] = 1, ["padStyle"] = "slot",
            ["randomDistribution"] = "uniform", ["lockWeights"] = false,
        };

    private static string OneOf(List<string> problems, string field, string value, string[] allowed)
    {
        if (allowed.Contains(value, StringComparer.Ordinal))
            return value;
        problems.Add($"{field} must be {string.Join(", ", allowed)}, not '{value}'");
        return allowed[0];
    }

    private static T Fail<T>(List<string> problems, string problem)
    {
        problems.Add(problem);
        return default!;
    }

    private static int WholeMs(double minutes) => (int)Math.Round(minutes * MinuteMs);

    private static McpException Problems(List<string> problems) =>
        new("The schedule has problems, nothing was sent to Tunarr: " + string.Join("; ", problems) + ".");

    [GeneratedRegex(@"^(?:(?<d>sun|mon|tue|wed|thu|fri|sat)[a-z]*\s+)?(?<h>\d{1,2}):(?<m>\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex AtPattern();
}
