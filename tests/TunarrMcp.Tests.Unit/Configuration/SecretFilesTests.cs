using Microsoft.Extensions.Configuration;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Tests.Unit.Configuration;

public sealed class SecretFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tunarr-mcp-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Reads_secret_from_file_and_strips_trailing_newline()
    {
        var path = Path.Combine(_dir, "totp_secret");
        File.WriteAllText(path, "JBSWY3DPEHPK3PXP\r\n");

        var resolved = SecretFiles.Resolve(Config(new() { ["Auth:TotpSecretFile"] = path }));

        Assert.Equal("JBSWY3DPEHPK3PXP", resolved["Auth:TotpSecret"]);
    }

    [Fact]
    public void Value_and_file_together_is_an_error()
    {
        var config = Config(new() { ["Auth:TotpSecret"] = "x", ["Auth:TotpSecretFile"] = "/run/secrets/totp" });

        var e = Assert.Throws<InvalidOperationException>(() => SecretFiles.Resolve(config));

        Assert.Contains("not both", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_file_names_the_key_and_path()
    {
        var missing = Path.Combine(_dir, "nope");

        var e = Assert.Throws<InvalidOperationException>(
            () => SecretFiles.Resolve(Config(new() { ["Auth:AdminPasswordHashFile"] = missing })));

        Assert.Contains("Auth:AdminPasswordHashFile", e.Message, StringComparison.Ordinal);
        Assert.Contains(missing, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_known_secret_keys_are_read()
    {
        var path = Path.Combine(_dir, "anything");
        File.WriteAllText(path, "contents");

        var resolved = SecretFiles.Resolve(Config(new() { ["Mcp:PublicUrlFile"] = path }));

        Assert.Empty(resolved);
    }
}
