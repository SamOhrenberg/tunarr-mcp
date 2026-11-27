using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TunarrMcp.Server.Auth;

/// <summary>
/// The one shape of client this server accepts: a public client (no secret) using the code flow with PKCE,
/// refresh tokens, and the MCP endpoint as its only resource. Client registration (DCR, CIMD) builds on this.
/// </summary>
internal static class McpClientDescriptor
{
    /// <summary>Application property recording how a client got here ("dcr" or "cimd"), for later cleanup.</summary>
    public const string RegistrationProperty = "registration";

    public static OpenIddictApplicationDescriptor Create(string clientId, string? displayName, IEnumerable<Uri> redirectUris, Uri resource)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            DisplayName = displayName,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + AuthScopes.Read,
                Permissions.Prefixes.Scope + AuthScopes.Write,
                Permissions.Prefixes.Resource + resource.AbsoluteUri,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        foreach (var uri in redirectUris)
            descriptor.RedirectUris.Add(uri);

        return descriptor;
    }
}
