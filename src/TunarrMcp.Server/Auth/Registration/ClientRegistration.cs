using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TunarrMcp.Server.Auth.Registration;

/// <summary>How clients get registered: metadata documents (CIMD, preferred by MCP) and RFC 7591 DCR (fallback).</summary>
internal static class ClientRegistration
{
    public static IServiceCollection AddClientRegistration(this IServiceCollection services)
    {
        services.AddSingleton<RedirectUriPolicy>();
        services.AddHttpClient<IClientMetadataFetcher, ClientMetadataFetcher>(http =>
            {
                http.Timeout = ClientMetadataFetcher.Timeout;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("tunarr-mcp");
            })
            .ConfigurePrimaryHttpMessageHandler(ClientMetadataFetcher.CreateHandler);
        return services;
    }

    /// <summary>
    /// Advertises both registration methods. Claude uses CIMD only when the metadata has both
    /// client_id_metadata_document_supported and "none" among the token endpoint auth methods.
    /// </summary>
    public static OpenIddictServerBuilder AdvertiseClientRegistration(this OpenIddictServerBuilder server) =>
        server.AddEventHandler<OpenIddictServerEvents.HandleConfigurationRequestContext>(handler => handler
            .UseInlineHandler(context =>
            {
                context.Metadata["registration_endpoint"] = new Uri(context.Issuer!, DynamicClientRegistration.Path.TrimStart('/')).AbsoluteUri;
                context.Metadata["client_id_metadata_document_supported"] = true;
                context.TokenEndpointAuthenticationMethods.Add(ClientAuthenticationMethods.None);
                return ValueTask.CompletedTask;
            })
            .SetOrder(OpenIddictServerHandlers.Discovery.AttachAdditionalMetadata.Descriptor.Order + 1_000));

    /// <summary>The CIMD middleware has to run before OpenIddict (UseAuthentication) sees the request.</summary>
    public static WebApplication UseClientMetadataDocuments(this WebApplication app)
    {
        app.UseWhen(ClientMetadataDocumentMiddleware.Applies, branch => branch.UseMiddleware<ClientMetadataDocumentMiddleware>());
        return app;
    }
}
