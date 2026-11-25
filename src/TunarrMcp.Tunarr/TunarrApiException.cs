using System.Net;

namespace TunarrMcp.Tunarr;

/// <summary>Tunarr answered with a non-success status. Carries enough detail for a tool to explain the failure.</summary>
public sealed class TunarrApiException : Exception
{
    private const int MaxDetailLength = 500;

    public TunarrApiException(HttpMethod method, string path, HttpStatusCode statusCode, string? detail)
        : base($"Tunarr {method} /{path} returned {(int)statusCode} {statusCode}." + (string.IsNullOrWhiteSpace(detail) ? "" : $" {detail}"))
    {
        Method = method;
        Path = path;
        StatusCode = statusCode;
        Detail = detail;
    }

    public HttpMethod Method { get; }

    public string Path { get; }

    public HttpStatusCode StatusCode { get; }

    /// <summary>Response body, truncated. Tunarr error bodies are short JSON messages, not secrets.</summary>
    public string? Detail { get; }

    internal static string Truncate(string body) =>
        body.Length <= MaxDetailLength ? body : body[..MaxDetailLength] + "…";
}
