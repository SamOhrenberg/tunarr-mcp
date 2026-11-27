using Microsoft.Extensions.Options;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Server.Auth.Registration;

/// <summary>
/// Which redirect URIs a client may use. Registration is open (anyone can POST /register or present a
/// metadata document), so this allowlist is what stops a stranger's client from receiving codes: even if
/// they got past the login, the code could only go to an allowed callback.
/// </summary>
internal sealed class RedirectUriPolicy
{
    /// <summary>Claude on the web, desktop and mobile; Claude Code's local callback (any port).</summary>
    public static readonly string[] Defaults =
    [
        "https://claude.ai/api/mcp/auth_callback",
        "http://localhost/callback",
        "http://127.0.0.1/callback",
    ];

    private readonly Uri[] _allowed;

    public RedirectUriPolicy(IOptions<AuthOptions> options)
    {
        var configured = AuthOptions.SplitList(options.Value.AllowedRedirectUris);
        _allowed = (configured.Length > 0 ? configured : Defaults).Select(u => new Uri(u, UriKind.Absolute)).ToArray();
    }

    public IReadOnlyList<Uri> Allowed => _allowed;

    public bool IsAllowed(Uri candidate) => _allowed.Any(a => Matches(a, candidate));

    /// <summary>
    /// Exact match on the whole URI. An http loopback entry matches any port on the same host and path:
    /// native apps such as Claude Code listen on a random free port (RFC 8252 §7.3).
    /// </summary>
    public static bool Matches(Uri allowed, Uri candidate)
    {
        if (!candidate.IsAbsoluteUri || candidate.UserInfo.Length > 0 || candidate.Fragment.Length > 0)
            return false;

        if (allowed.Scheme == Uri.UriSchemeHttp && allowed.IsLoopback)
            return candidate.Scheme == Uri.UriSchemeHttp
                   && string.Equals(candidate.Host, allowed.Host, StringComparison.OrdinalIgnoreCase)
                   && candidate.PathAndQuery == allowed.PathAndQuery;

        return candidate.AbsoluteUri == allowed.AbsoluteUri;
    }
}
