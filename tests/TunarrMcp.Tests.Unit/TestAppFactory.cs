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

internal static class TestAppFactoryExtensions
{
    /// <summary>The app in Auth:Mode=Embedded, with its SQLite database and keys in <paramref name="dataPath"/>.</summary>
    public static WebApplicationFactory<Program> WithEmbeddedAuth(this WebApplicationFactory<Program> factory, string dataPath) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:Mode", "Embedded");
            b.UseSetting("Auth:AllowInsecureNone", "false");
            b.UseSetting("Auth:AdminUser", "admin");
            b.UseSetting("Auth:AdminPasswordHash", "test-hash");
            b.UseSetting("Auth:TotpSecret", "JBSWY3DPEHPK3PXP");
            b.UseSetting("Data:Path", dataPath);
        });
}
