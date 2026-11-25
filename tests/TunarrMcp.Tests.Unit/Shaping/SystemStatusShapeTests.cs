using TunarrMcp.Server.Shaping;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Tests.Unit.Shaping;

public sealed class SystemStatusShapeTests
{
    private static readonly TestClock Chicago = new(TimeSpan.FromHours(-6));

    private static readonly TunarrVersion Version = new("2026.11.2", "8.1.2", null);

    private static readonly GuideStatus NoGuide = new(
        new Dictionary<string, DateTimeOffset>(), new Dictionary<string, GuideTimes>(), []);

    private static SystemStatus Shape(
        Dictionary<string, HealthCheckResult>? health = null,
        IReadOnlyList<TunarrTask>? tasks = null,
        GuideStatus? guide = null) =>
        SystemStatusShape.From(Version, health ?? [], tasks ?? [], guide ?? NoGuide, Chicago);

    [Fact]
    public void Only_non_healthy_checks_are_listed()
    {
        var status = Shape(new()
        {
            ["FfmpegVersion"] = new("healthy", null),
            ["MissingSeasonNumbers"] = new("warning", "3 shows"),
        });

        var issue = Assert.Single(status.HealthIssues);
        Assert.Equal(new HealthIssue("MissingSeasonNumbers", "warning", "3 shows"), issue);
        Assert.False(status.Healthy);
    }

    [Fact]
    public void Info_level_checks_do_not_make_tunarr_unhealthy()
    {
        var status = Shape(new() { ["HardwareAcceleration"] = new("info", "No hardware acceleration") });

        Assert.True(status.Healthy);
        Assert.Single(status.HealthIssues);
    }

    [Fact]
    public void Task_schedules_merge_and_times_are_local()
    {
        var tasks = new[]
        {
            new TunarrTask("ScanLibrariesTask", "ScanLibrariesTask", "Scan", [
                new ScheduledTask(false, At(12, 0), At(18, 0)),
                new ScheduledTask(true, At(13, 0), At(19, 0)),
            ]),
        };

        var task = Assert.Single(Shape(tasks: tasks).Tasks);

        Assert.True(task.Running);
        Assert.Equal(At(13, 0), task.LastRun);
        Assert.Equal(At(18, 0), task.NextRun);
        Assert.Equal(TimeSpan.FromHours(-6), task.NextRun!.Value.Offset);
    }

    [Fact]
    public void On_demand_task_has_no_times()
    {
        var task = Assert.Single(Shape(tasks: [new TunarrTask("BackupTask", "BackupTask", "Backup", [])]).Tasks);

        Assert.False(task.Running);
        Assert.Null(task.LastRun);
        Assert.Null(task.NextRun);
    }

    [Fact]
    public void Guide_reports_stalest_update_and_shortest_coverage()
    {
        var guide = new GuideStatus(
            new Dictionary<string, DateTimeOffset> { ["a"] = At(12, 0), ["b"] = At(11, 0) },
            new Dictionary<string, GuideTimes> { ["a"] = new(At(10, 0), At(23, 0)), ["b"] = new(At(10, 0), At(22, 0)) },
            ["a", "b"]);

        var summary = Shape(guide: guide).Guide;

        Assert.Equal(new GuideSummary(2, At(11, 0), At(22, 0)), summary);
    }

    // Tunarr sends UTC here; the shape must convert to the clock's zone (-06:00) without changing the instant.
    private static DateTimeOffset At(int hour, int minute) =>
        new DateTimeOffset(2026, 11, 27, hour, minute, 0, TimeSpan.Zero);
}

internal sealed class TestClock(TimeSpan offset) : TimeProvider
{
    public override TimeZoneInfo LocalTimeZone { get; } =
        TimeZoneInfo.CreateCustomTimeZone("Test", offset, "Test", "Test");
}
