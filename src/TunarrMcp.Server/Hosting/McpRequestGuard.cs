using System.Net;
using Microsoft.Extensions.Options;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Server.Hosting;

/// <summary>
/// Cheap checks on the MCP endpoint that run before authentication, in every Auth mode:
///   - Origin: the MCP spec requires rejecting unexpected Origin headers, which stops a web page from
///     driving the server through DNS rebinding. Server-side clients (Claude's cloud, Claude Code) send none.
///   - Auth:AllowedIpRanges: an optional allowlist. Behind a proxy this relies on forwarded headers
///     (ASPNETCORE_FORWARDEDHEADERS_ENABLED=true), so RemoteIpAddress is the real client.
/// </summary>
internal sealed class McpRequestGuard(RequestDelegate next, IOptions<McpOptions> mcp, IOptions<AuthOptions> auth)
{
    private readonly string _ownOrigin = mcp.Value.Resource.GetLeftPart(UriPartial.Authority);
    private readonly IReadOnlyList<IPNetwork> _allowedRanges = auth.Value.ParseAllowedIpRanges();

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.Origin is { Count: > 0 } origin
            && !string.Equals(origin.ToString(), _ownOrigin, StringComparison.OrdinalIgnoreCase))
            return Reject(context, "Origin not allowed.");

        if (_allowedRanges.Count > 0 && !IsAllowed(context.Connection.RemoteIpAddress))
            return Reject(context, "Client address not allowed.");

        return next(context);
    }

    private bool IsAllowed(IPAddress? address)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return _allowedRanges.Any(r => r.Contains(address));
    }

    private static Task Reject(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsync(reason, context.RequestAborted);
    }
}

internal static class McpRequestGuardExtensions
{
    public static WebApplication UseMcpRequestGuard(this WebApplication app)
    {
        var path = app.Services.GetRequiredService<IOptions<McpOptions>>().Value.Path;
        app.UseWhen(c => c.Request.Path.StartsWithSegments(path), branch => branch.UseMiddleware<McpRequestGuard>());
        return app;
    }
}
