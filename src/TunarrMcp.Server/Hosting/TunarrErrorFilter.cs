using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Polly.CircuitBreaker;
using Polly.Timeout;
using TunarrMcp.Tunarr;

namespace TunarrMcp.Server.Hosting;

/// <summary>
/// Turns Tunarr failures into tool errors Claude can read and act on. Without this the SDK reports a generic
/// "An error occurred invoking 'tool'". Any other exception is left to the SDK, so unexpected bugs don't leak details.
/// </summary>
internal static class TunarrErrorFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (TunarrApiException e)
            {
                throw new McpException(e.Message, e);
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutRejectedException or BrokenCircuitException)
            {
                throw new McpException("Tunarr is not reachable right now. Try again in a minute.", e);
            }
        };
}
