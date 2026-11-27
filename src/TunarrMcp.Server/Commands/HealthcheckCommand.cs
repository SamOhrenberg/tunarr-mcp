namespace TunarrMcp.Server.Commands;

/// <summary>
/// <c>tunarr-mcp --healthcheck [URL]</c>: exits 0 if the server's liveness endpoint answers. The chiseled image
/// has no shell or curl, so Docker's HEALTHCHECK runs the app itself:
/// <c>HEALTHCHECK CMD ["dotnet", "TunarrMcp.Server.dll", "--healthcheck"]</c>.
/// </summary>
internal static class HealthcheckCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var url = args.Length > 0 ? args[0] : DefaultUrl();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await http.GetAsync(new Uri(url)).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    // The .NET container images set ASPNETCORE_HTTP_PORTS (8080 by default).
    internal static string DefaultUrl()
    {
        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        var port = ports?.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "8080";
        return $"http://127.0.0.1:{port}/healthz";
    }
}
