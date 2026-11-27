using System.Net;

namespace TunarrMcp.Server.Configuration;

public enum AuthMode
{
    /// <summary>Built-in OAuth authorization server with a single admin login.</summary>
    Embedded,

    /// <summary>An external OpenID Connect provider issues the tokens; this server only validates them.</summary>
    ExternalOidc,

    /// <summary>No authentication. Refused unless <see cref="AuthOptions.AllowInsecureNone"/> is set.</summary>
    None,
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    public AuthMode Mode { get; set; } = AuthMode.Embedded;

    /// <summary>Explicit opt-in required for <see cref="AuthMode.None"/>.</summary>
    public bool AllowInsecureNone { get; set; }

    // Embedded mode
    public string? AdminUser { get; set; }

    /// <summary>Secret. Also accepts <c>Auth:AdminPasswordHashFile</c>.</summary>
    public string? AdminPasswordHash { get; set; }

    /// <summary>Secret. Base32 TOTP seed. Also accepts <c>Auth:TotpSecretFile</c>.</summary>
    public string? TotpSecret { get; set; }

    // ExternalOidc mode
    public Uri? Authority { get; set; }

    /// <summary>Expected token audience. Defaults to <see cref="McpOptions.Resource"/> when unset.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Comma-separated redirect URIs a client may register (DCR) or declare (CIMD). Exact match, except that
    /// http loopback entries (localhost, 127.0.0.1, [::1]) match any port. Defaults to Claude's callbacks.
    /// </summary>
    public string? AllowedRedirectUris { get; set; }

    /// <summary>Optional comma-separated CIDR allowlist for /mcp, for example "160.79.104.0/21, 203.0.113.7/32".</summary>
    public string? AllowedIpRanges { get; set; }

    public IReadOnlyList<IPNetwork> ParseAllowedIpRanges() =>
        SplitList(AllowedIpRanges).Select(IPNetwork.Parse).ToArray();

    internal static string[] SplitList(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
