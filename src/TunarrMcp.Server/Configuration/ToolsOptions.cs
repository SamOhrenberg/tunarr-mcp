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
}
