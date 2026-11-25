namespace TunarrMcp.Server.Configuration;

public sealed class DataOptions
{
    public const string Section = "Data";

    /// <summary>Directory for the SQLite database and signing keys. Mount a volume here.</summary>
    public string Path { get; set; } = "/data";
}
