using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Server.Auth.Registration;

/// <summary>
/// CIMD support. Runs ahead of OpenIddict on GET /connect/authorize: when the client_id is an https URL, the
/// document is fetched and validated, and the client is created or updated from it, so OpenIddict then
/// validates the request against a normal registered client. The document is re-read on every authorization
/// (they're rare), so changes to it take effect without any cache to expire.
/// </summary>
internal sealed class ClientMetadataDocumentMiddleware(RequestDelegate next)
{
    public static bool Applies(HttpContext context) =>
        HttpMethods.IsGet(context.Request.Method)
        && context.Request.Path == "/connect/authorize"
        && ClientMetadataDocument.IsDocumentUrl(context.Request.Query["client_id"]);

    public async Task InvokeAsync(
        HttpContext context, IClientMetadataFetcher fetcher, IOpenIddictApplicationManager applications,
        RedirectUriPolicy redirects, IOptions<McpOptions> mcp)
    {
        var clientId = context.Request.Query["client_id"].ToString();
        Uri.TryCreate(context.Request.Query["redirect_uri"], UriKind.Absolute, out var requestedRedirect);

        try
        {
            var url = ClientMetadataDocument.ValidateUrl(clientId);
            var json = await fetcher.FetchAsync(url, context.RequestAborted).ConfigureAwait(false);
            var document = ClientMetadataDocument.Parse(url, json);

            var descriptor = McpClientDescriptor.Create(clientId, document.ClientName,
                document.AllowedRedirectUris(redirects, requestedRedirect), mcp.Value.Resource);
            descriptor.Properties[McpClientDescriptor.RegistrationProperty] = JsonSerializer.SerializeToElement("cimd");

            var existing = await applications.FindByClientIdAsync(clientId, context.RequestAborted).ConfigureAwait(false);
            if (existing is null)
                await applications.CreateAsync(descriptor, context.RequestAborted).ConfigureAwait(false);
            else
                await applications.UpdateAsync(existing, descriptor, context.RequestAborted).ConfigureAwait(false);
        }
        catch (ClientMetadataException e)
        {
            // Not redirected to the client: its redirect URI hasn't been validated, so this is shown in the browser.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new Dictionary<string, string> { ["error"] = "invalid_client", ["error_description"] = e.Message },
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
