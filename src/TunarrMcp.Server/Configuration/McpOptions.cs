namespace TunarrMcp.Server.Configuration;

/// <summary>
/// The public face of the server. The OAuth resource, issuer and endpoint path are all derived from
/// <see cref="PublicUrl"/>, so a deployment only has to set one URL.
/// </summary>
public sealed class McpOptions
{
    public const string Section = "Mcp";

    /// <summary>Public URL of the MCP endpoint, for example https://mcp.example.com/mcp.</summary>
    public Uri? PublicUrl { get; set; }

    /// <summary>OAuth protected-resource identifier. Must equal the URL clients connect to, exactly.</summary>
    public Uri Resource => PublicUrl ?? throw NotConfigured();

    /// <summary>OAuth issuer: the origin of <see cref="PublicUrl"/> with a trailing slash.</summary>
    public Uri Issuer => new(Resource.GetLeftPart(UriPartial.Authority) + "/");

    /// <summary>Route the MCP endpoint is mapped on, for example /mcp.</summary>
    public string Path => Resource.AbsolutePath;

    private static InvalidOperationException NotConfigured() =>
        new($"{Section}:{nameof(PublicUrl)} is not configured.");
}
