namespace TunarrMcp.Server.Configuration;

public sealed class TunarrOptions
{
    public const string Section = "Tunarr";

    /// <summary>Base URL of Tunarr's REST API. Normalized to end with a slash.</summary>
    public Uri BaseUrl { get; set; } = new("http://tunarr:8000/api/");

    /// <summary>Only for Tunarr installs with TUNARR_BASIC_AUTH_* set.</summary>
    public string? BasicAuthUser { get; set; }

    /// <summary>Secret. Also accepts <c>Tunarr:BasicAuthPasswordFile</c>.</summary>
    public string? BasicAuthPassword { get; set; }

    public bool UsesBasicAuth => !string.IsNullOrEmpty(BasicAuthUser);
}
