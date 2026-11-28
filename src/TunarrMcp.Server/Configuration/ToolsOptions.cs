namespace TunarrMcp.Server.Configuration;

public enum ToolsMode
{
    /// <summary>Read tools only. Write tools are not registered, so clients never see them.</summary>
    Read,

    /// <summary>Read and write tools, except the destructive ones.</summary>
    Write,

    /// <summary>Everything, including destructive tools such as delete_item.</summary>
    All,
}

public sealed class ToolsOptions
{
    public const string Section = "Tools";

    public ToolsMode Mode { get; set; } = ToolsMode.Read;

    /// <summary>
    /// Optional comma-separated channel numbers or ids that write tools may change, for example "99". Unset means
    /// every channel. Read tools see every channel either way.
    /// </summary>
    public string? WritableChannels { get; set; }

    public IReadOnlyList<string> ParseWritableChannels() => AuthOptions.SplitList(WritableChannels);
}
