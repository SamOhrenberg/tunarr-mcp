namespace TunarrMcp.Tunarr.Models;

/// <summary>GET /version</summary>
public sealed record TunarrVersion(string Tunarr, string Ffmpeg, string? EtvNext);

/// <summary>One entry of GET /system/health. Type is healthy, info, warning or error; Context is set for all but healthy.</summary>
public sealed record HealthCheckResult(string Type, string? Context)
{
    public bool IsHealthy => Type == "healthy";
}

/// <summary>One entry of GET /tasks. ScheduledTasks is empty for tasks that only run on demand (e.g. BackupTask).</summary>
public sealed record TunarrTask(string Id, string Name, string? Description, IReadOnlyList<ScheduledTask> ScheduledTasks);

public sealed record ScheduledTask(bool Running, DateTimeOffset? LastExecution, DateTimeOffset? NextExecution);

/// <summary>GET /guide/status. Dictionaries are keyed by channel id.</summary>
public sealed record GuideStatus(
    IReadOnlyDictionary<string, DateTimeOffset> LastUpdate,
    IReadOnlyDictionary<string, GuideTimes> GuideTimes,
    IReadOnlyList<string> ChannelIds);

public sealed record GuideTimes(DateTimeOffset Start, DateTimeOffset End);
