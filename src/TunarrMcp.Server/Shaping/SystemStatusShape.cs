using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Server.Shaping;

public sealed record SystemStatus(
    string TunarrVersion,
    string FfmpegVersion,
    bool Healthy,
    IReadOnlyList<HealthIssue> HealthIssues,
    IReadOnlyList<ScheduledTaskStatus> Tasks,
    GuideSummary Guide);

/// <summary>A Tunarr health check that is not "healthy". Level is info, warning or error.</summary>
public sealed record HealthIssue(string Check, string Level, string? Message);

/// <summary>Running and the times merge every schedule of the task. LastRun/NextRun are null for on-demand tasks.</summary>
public sealed record ScheduledTaskStatus(string Id, string? Description, bool Running, DateTimeOffset? LastRun, DateTimeOffset? NextRun);

/// <param name="Channels">Channels with a guide.</param>
/// <param name="OldestUpdate">The stalest channel's last guide rebuild.</param>
/// <param name="BuiltThrough">Every channel's guide covers at least up to this time.</param>
public sealed record GuideSummary(int Channels, DateTimeOffset? OldestUpdate, DateTimeOffset? BuiltThrough);

internal static class SystemStatusShape
{
    public static SystemStatus From(
        TunarrVersion version,
        IReadOnlyDictionary<string, HealthCheckResult> health,
        IReadOnlyList<TunarrTask> tasks,
        GuideStatus guide,
        TimeProvider clock)
    {
        var issues = health
            .Where(h => !h.Value.IsHealthy)
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => new HealthIssue(h.Key, h.Value.Type, h.Value.Context))
            .ToArray();

        return new SystemStatus(
            version.Tunarr,
            version.Ffmpeg,
            Healthy: issues.All(i => i.Level == "info"),
            issues,
            tasks.Select(t => Task(t, clock)).ToArray(),
            Guide(guide, clock));
    }

    private static ScheduledTaskStatus Task(TunarrTask task, TimeProvider clock) => new(
        task.Id,
        task.Description,
        Running: task.ScheduledTasks.Any(s => s.Running),
        LastRun: task.ScheduledTasks.Max(s => s.LastExecution).ToLocal(clock),
        NextRun: task.ScheduledTasks.Min(s => s.NextExecution).ToLocal(clock));

    private static GuideSummary Guide(GuideStatus guide, TimeProvider clock) => new(
        guide.ChannelIds.Count,
        OldestUpdate: guide.LastUpdate.Count == 0 ? null : guide.LastUpdate.Values.Min().ToLocal(clock),
        BuiltThrough: guide.GuideTimes.Count == 0 ? null : guide.GuideTimes.Values.Min(g => g.End).ToLocal(clock));
}
