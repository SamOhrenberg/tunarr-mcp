namespace TunarrMcp.Tunarr.Models;

/// <summary>GET /version</summary>
public sealed record TunarrVersion(string Tunarr, string Ffmpeg, string? EtvNext);

/// <summary>One entry of GET /system/health. Type is healthy, info, warning or error; Context is set for all but healthy.</summary>
public sealed record HealthCheckResult(string Type, string? Context)
{
    public bool IsHealthy => Type == "healthy";
}
