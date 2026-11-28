using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Shaping;

/// <param name="Programming">time_slots or random_slots.</param>
/// <param name="GeneratedItems">Items in the lineup the schedule generates; it loops after that.</param>
/// <param name="GeneratedDays">How long that lineup runs.</param>
/// <param name="FlexPercent">Share of the previewed window that is flex (offline screen or gaps).</param>
/// <param name="Upcoming">What would air from now, in the server's local time.</param>
public sealed record SchedulePreviewView(
    int Channel,
    string Name,
    string Programming,
    int GeneratedItems,
    double GeneratedDays,
    double FlexPercent,
    IReadOnlyList<PreviewItem> Upcoming,
    string Note);

/// <param name="Type">movie, episode, other_video, music_video, track, flex or redirect.</param>
public sealed record PreviewItem(
    DateTimeOffset StartsAt,
    string Type,
    string? Title,
    string? Show,
    int? SeasonNumber,
    int? EpisodeNumber,
    double Minutes);

internal static class PreviewShape
{
    private const int MaxItems = 300;

    public static SchedulePreviewView From(Channel channel, string programming, SchedulePreview preview, TimeSpan window, TimeProvider clock)
    {
        var items = preview.Lineup;
        var total = items.Sum(i => i.Duration);
        var upcoming = new List<PreviewItem>();
        double flexMs = 0, windowMs = 0;

        if (items.Count > 0 && total > 0)
        {
            // Tunarr plays the lineup from the channel's start time, looping; find what's on now.
            var start = DateTimeOffset.FromUnixTimeMilliseconds((long)preview.StartTime);
            var now = clock.GetUtcNow();
            var index = 0;
            var at = start;
            if (now > start)
            {
                var into = (now - start).TotalMilliseconds % total;
                at = now.AddMilliseconds(-into);
                while (into >= items[index].Duration)
                {
                    into -= items[index].Duration;
                    at = at.AddMilliseconds(items[index].Duration);
                    index++;
                }
            }

            var end = (now > start ? now : start) + window;
            while (at < end && upcoming.Count < MaxItems)
            {
                var item = items[index];
                var view = LineupShape.Item(item, index, 0, preview.Programs);
                upcoming.Add(new PreviewItem(WholeSeconds(at).ToLocal(clock), view.Type, view.Title, view.Show, view.SeasonNumber, view.EpisodeNumber, view.Minutes));

                var shown = Math.Min(item.Duration, (end - at).TotalMilliseconds);
                windowMs += shown;
                if (item.Type == "flex")
                    flexMs += shown;

                at = at.AddMilliseconds(item.Duration);
                index = (index + 1) % items.Count;
            }
        }

        return new SchedulePreviewView(
            channel.Number,
            channel.Name,
            programming,
            items.Count,
            Math.Round(total / TimeSpan.MillisecondsPerDay, 1),
            windowMs == 0 ? 0 : Math.Round(100 * flexMs / windowMs, 1),
            upcoming,
            "Nothing was saved. Shuffled slots pick differently each time the schedule is generated.");
    }

    // Program lengths aren't whole seconds; sub-second start times are noise to a reader.
    private static DateTimeOffset WholeSeconds(DateTimeOffset at) =>
        new(at.Ticks - (at.Ticks % TimeSpan.TicksPerSecond), at.Offset);
}
