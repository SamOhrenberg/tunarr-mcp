using System.Text.Json;

namespace TunarrMcp.Server.Auth.Registration;

internal sealed class ClientMetadataException(string message) : Exception(message);

/// <summary>
/// An OAuth Client ID Metadata Document (draft-ietf-oauth-client-id-metadata-document): the client_id is an
/// https URL, and the JSON document served there describes the client. Only the fields this server uses are read.
/// </summary>
internal sealed record ClientMetadataDocument(Uri ClientId, string? ClientName, IReadOnlyList<Uri> RedirectUris)
{
    private const int MaxClientNameLength = 100;

    /// <summary>Whether a client_id is in the URL form that selects CIMD (as opposed to a registered id).</summary>
    public static bool IsDocumentUrl(string? clientId) =>
        clientId is not null && clientId.StartsWith("https://", StringComparison.Ordinal);

    public static Uri ValidateUrl(string clientId)
    {
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new ClientMetadataException("The client_id must be an https URL.");
        if (url.UserInfo.Length > 0 || url.Fragment.Length > 0)
            throw new ClientMetadataException("The client_id URL must not contain credentials or a fragment.");
        if (url.AbsolutePath == "/")
            throw new ClientMetadataException("The client_id URL must have a path.");
        // Uri silently resolves "a/../b", lowercases the host and re-escapes. Requiring the canonical form rejects
        // all of those, so the id we store is exactly the URL we fetched.
        if (url.AbsoluteUri != clientId)
            throw new ClientMetadataException("The client_id URL must be in canonical form (no dot segments, lowercase host).");
        return url;
    }

    public static ClientMetadataDocument Parse(Uri url, string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ClientMetadataException("The client metadata document is not valid JSON.");
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new ClientMetadataException("The client metadata document must be a JSON object.");
        if (String(root, "client_id") != url.OriginalString)
            throw new ClientMetadataException("The document's client_id does not match the URL it was fetched from.");
        if (root.TryGetProperty("client_secret", out _))
            throw new ClientMetadataException("A client metadata document must not contain a client_secret.");
        if (String(root, "token_endpoint_auth_method") is not (null or "none"))
            throw new ClientMetadataException("Only public clients are supported (token_endpoint_auth_method \"none\").");

        if (!root.TryGetProperty("redirect_uris", out var redirects) || redirects.ValueKind != JsonValueKind.Array || redirects.GetArrayLength() == 0)
            throw new ClientMetadataException("The document must list redirect_uris.");

        var redirectUris = new List<Uri>();
        foreach (var item in redirects.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Uri.TryCreate(item.GetString(), UriKind.Absolute, out var uri))
                throw new ClientMetadataException("Every redirect_uris entry must be an absolute URL.");
            redirectUris.Add(uri);
        }

        var name = String(root, "client_name")?.Trim();
        return new ClientMetadataDocument(url, string.IsNullOrEmpty(name) ? null : name[..Math.Min(name.Length, MaxClientNameLength)], redirectUris);
    }

    /// <summary>
    /// The redirect URIs to register: the document's, narrowed to what this server allows. If the authorization
    /// request uses a loopback URI on a specific port that the document lists without one, that exact URI is
    /// added too, since OpenIddict compares redirect URIs exactly.
    /// </summary>
    public IReadOnlyList<Uri> AllowedRedirectUris(RedirectUriPolicy policy, Uri? requested)
    {
        var allowed = RedirectUris.Where(policy.IsAllowed).ToList();
        if (allowed.Count == 0)
            throw new ClientMetadataException("None of the document's redirect_uris are allowed by this server.");

        if (requested is not null && !allowed.Contains(requested) && allowed.Any(a => RedirectUriPolicy.Matches(a, requested)))
            allowed.Add(requested);

        return allowed;
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
