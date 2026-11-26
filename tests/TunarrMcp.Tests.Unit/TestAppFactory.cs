using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
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

/// <summary>The admin credentials every Embedded-mode test signs in with.</summary>
internal static class TestAdmin
{
    public const string User = "admin";
    public const string Password = "correct horse battery staple";
    public const string TotpSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP"; // 160 bits

    public static readonly string PasswordHash = new PasswordHasher<object>().HashPassword(new object(), Password);
}

internal static class TestAppFactoryExtensions
{
    /// <summary>The app in Auth:Mode=Embedded, with its SQLite database and keys in <paramref name="dataPath"/>.</summary>
    public static WebApplicationFactory<Program> WithEmbeddedAuth(this WebApplicationFactory<Program> factory, string dataPath) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:Mode", "Embedded");
            b.UseSetting("Auth:AllowInsecureNone", "false");
            b.UseSetting("Auth:AdminUser", TestAdmin.User);
            b.UseSetting("Auth:AdminPasswordHash", TestAdmin.PasswordHash);
            b.UseSetting("Auth:TotpSecret", TestAdmin.TotpSecret);
            b.UseSetting("Data:Path", dataPath);
        });
}
