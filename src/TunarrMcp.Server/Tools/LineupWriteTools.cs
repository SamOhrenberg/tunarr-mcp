using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TunarrMcp.Server.Changes;
using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Tools;

/// <param name="Added">What was appended, in order.</param>
/// <param name="TotalItems">Items in the lineup now.</param>
public sealed record AddToChannelResult(
    int Channel,
    string Name,
    IReadOnlyList<string> Added,
    double AddedMinutes,
    int TotalItems,
    string Note);

/// <param name="Undone">The change that was reverted.</param>
/// <param name="TotalItems">Items in the restored lineup.</param>
/// <param name="MoreToUndo">Earlier changes on this channel that undo_last_change can still revert.</param>
public sealed record UndoResult(int Channel, string Name, string Undone, DateTimeOffset MadeAt, int TotalItems, int MoreToUndo);

/// <summary>
/// Writes to a channel's lineup. Every write snapshots the lineup first and records it in the <see cref="ChangeLog"/>,
/// which is what makes these non-destructive, and so available in Tools:Mode=write.
/// </summary>
[McpServerToolType]
public sealed class LineupWriteTools(TunarrClient tunarr, ChangeLog changes, WritableChannels writable, TimeProvider clock)
{
    private const int MaxPrograms = 200;
    private static readonly HashSet<string> Playable = ["movie", "episode", "other_video", "music_video", "track"];

    [McpServerTool(Name = "add_to_channel", Title = "Add programs to a channel", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("""
        Appends specific episodes or movies to the end of a channel's lineup, in the order given, like "Add media" in
        Tunarr. Get ids from search_library or get_show (with a season). The same id may be given more than once.
        The lineup is snapshotted first: undo_last_change reverts this.
        """)]
    public async Task<AddToChannelResult> AddToChannel(
        [Description("Channel number, name or id.")] string channel,
        [Description("Episode or movie ids, in the order they should play.")] string[] programIds,
        [Description("""
            Needed for a channel programmed by a time-slot or random-slot schedule. Tunarr keeps the schedule, but
            the lineup becomes hand-edited, and the next time the schedule is saved the lineup is regenerated without
            these additions.
            """)] bool allowOnScheduledChannel = false,
        CancellationToken cancellationToken = default)
    {
        if (programIds.Length is 0 or > MaxPrograms)
            throw new McpException($"Give between 1 and {MaxPrograms} program ids.");

        var target = await ChannelLookup.FindAsync(tunarr, channel, cancellationToken).ConfigureAwait(false);
        writable.EnsureWritable(target);

        var schedule = await tunarr.GetChannelScheduleAsync(target.Id, cancellationToken).ConfigureAwait(false);
        if (schedule?.Schedule is { } slots && !allowOnScheduledChannel)
        {
            throw new McpException(
                $"Channel {target.Number} {target.Name} is programmed by {(slots.Type == "time" ? "time" : "random")} slots. " +
                "Adding programs by hand makes its lineup hand-edited, and the next schedule save regenerates the lineup " +
                "without them. To add them anyway, call again with allowOnScheduledChannel: true; to change what the " +
                "schedule plays, edit the schedule instead.");
        }

        var programs = await LookUpAsync(programIds, cancellationToken).ConfigureAwait(false);

        return await changes.RunExclusiveAsync(async () =>
        {
            var before = await tunarr.GetLineupItemsJsonAsync(target.Id, cancellationToken).ConfigureAwait(false);
            var saved = await tunarr.AppendToLineupAsync(target.Id, programs, cancellationToken).ConfigureAwait(false);

            var added = programs.Select(Describe).ToArray();
            var minutes = Math.Round(programs.Sum(p => p.Duration ?? 0) / TimeSpan.MillisecondsPerMinute, 1);
            await changes.RecordAsync(new ChannelChange
            {
                ChannelId = target.Id,
                ChannelNumber = target.Number,
                ChannelName = target.Name,
                Tool = "add_to_channel",
                Summary = programs.Count == 1 ? $"Added {added[0]}" : $"Added {programs.Count} programs ({minutes} min), starting with {added[0]}",
                Before = JsonSerializer.Serialize(new ChannelState(JsonDocument.Parse(before).RootElement), JsonSerializerOptions.Web),
                AfterFingerprint = LineupFingerprint.Of(saved.Lineup),
            }, cancellationToken).ConfigureAwait(false);

            return new AddToChannelResult(target.Number, target.Name, added, minutes, saved.TotalPrograms,
                schedule?.Schedule is null
                    ? "Appended at the end of the lineup. undo_last_change reverts it."
                    : "Appended at the end of the hand-edited lineup; saving the schedule will regenerate the lineup without them. undo_last_change reverts it.");
        }, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "undo_last_change", Title = "Undo the last change", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("""
        Reverts the most recent change a tool made to a channel (or to any channel), restoring its lineup exactly as it
        was. Call again to step further back. Refuses if the channel was edited some other way since, unless force is
        set, because those edits would be lost too.
        """)]
    public async Task<UndoResult> UndoLastChange(
        [Description("Channel number, name or id. Leave out to undo the latest change on any channel.")] string? channel = null,
        [Description("Undo even though the channel changed since, discarding those later edits.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var target = channel is null ? null : await ChannelLookup.FindAsync(tunarr, channel, cancellationToken).ConfigureAwait(false);

        return await changes.RunExclusiveAsync(async () =>
        {
            var change = await changes.LatestAsync(target?.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new McpException(target is null
                    ? "There is nothing to undo."
                    : $"There is nothing to undo on channel {target.Number} {target.Name}.");

            var channelNow = target ?? await ChannelLookup.FindAsync(tunarr, change.ChannelId, cancellationToken).ConfigureAwait(false);
            writable.EnsureWritable(channelNow);

            var current = await tunarr.GetLineupItemsJsonAsync(change.ChannelId, cancellationToken).ConfigureAwait(false);
            if (!force && LineupFingerprint.Of(current) != change.AfterFingerprint)
            {
                throw new McpException(
                    $"Channel {channelNow.Number} {channelNow.Name} changed after \"{change.Summary}\" " +
                    $"({change.MadeAt.ToLocal(clock).ToString("g", CultureInfo.InvariantCulture)}), outside these tools. " +
                    "Undoing would discard those edits too. Call again with force: true to undo anyway.");
            }

            var state = JsonSerializer.Deserialize<ChannelState>(change.Before, JsonSerializerOptions.Web)!;
            var restored = await tunarr.ReplaceLineupAsync(change.ChannelId, state.Lineup.GetRawText(), cancellationToken).ConfigureAwait(false);
            await changes.MarkUndoneAsync(change.Id, cancellationToken).ConfigureAwait(false);

            return new UndoResult(channelNow.Number, channelNow.Name, change.Summary, change.MadeAt.ToLocal(clock),
                restored.TotalPrograms, await changes.CountUndoableAsync(change.ChannelId, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Looks every id up in Tunarr (for its real duration) and fails with all the problems at once.</summary>
    private async Task<IReadOnlyList<ProgramDetails>> LookUpAsync(string[] ids, CancellationToken ct)
    {
        var unique = ids.Distinct(StringComparer.Ordinal).ToArray();
        var found = new Dictionary<string, ProgramDetails?>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(unique, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (id, token) =>
        {
            var program = await tunarr.GetProgramAsync(id, token).ConfigureAwait(false);
            lock (found)
                found[id] = program;
        }).ConfigureAwait(false);

        var problems = new List<string>();
        var unknown = unique.Where(id => found[id] is null).ToArray();
        if (unknown.Length > 0)
            problems.Add($"Not episodes or movies: {string.Join(", ", unknown)} (show and season ids don't work here; get_show lists a season's episode ids)");
        var unplayable = unique.Select(id => found[id]).OfType<ProgramDetails>()
            .Where(p => !Playable.Contains(p.Type) || p.State is not (null or "ok") || p.Duration is not > 0).ToArray();
        if (unplayable.Length > 0)
            problems.Add("Can't be played (missing from the media server, or no runtime): " + string.Join(", ", unplayable.Select(p => $"{Describe(p)} [{p.Uuid}]")));
        if (problems.Count > 0)
            throw new McpException(string.Join(". ", problems) + ". Nothing was added.");

        return ids.Select(id => found[id]!).ToArray();
    }

    internal static string Describe(ProgramDetails p) => p.Type == "episode" && p.Show?.Title is { } show
        ? $"{show} S{p.Season?.Index?.ToString(CultureInfo.InvariantCulture) ?? "?"}E{p.EpisodeNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"} {p.Title}"
        : p.Year is { } year ? $"{p.Title} ({year})" : p.Title;
}
