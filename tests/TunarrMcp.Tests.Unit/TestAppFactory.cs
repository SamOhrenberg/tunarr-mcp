using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TunarrMcp.Tests.Unit;

/// <summary>Hosts the app in-process with a minimal valid configuration, independent of appsettings.Development.json.</summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Mcp:PublicUrl", "http://localhost/mcp");
        builder.UseSetting("Tunarr:BaseUrl", "http://tunarr.test/api/");
        builder.UseSetting("Auth:Mode", "None");
        builder.UseSetting("Auth:AllowInsecureNone", "true");
    }
}
