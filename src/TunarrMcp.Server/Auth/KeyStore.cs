using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace TunarrMcp.Server.Auth;

/// <summary>
/// RSA keys persisted as PEM files on the data volume, so tokens stay valid across restarts and image upgrades.
/// Created on first start. The key id is the RFC 7638 thumbprint, so it's stable for a given key.
/// </summary>
internal static class KeyStore
{
    public static RsaSecurityKey LoadOrCreate(string directory, string name)
    {
        var path = Path.Combine(directory, $"{name}.pem");
        var rsa = RSA.Create();

        if (File.Exists(path))
        {
            rsa.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            rsa.KeySize = 2048;
            Directory.CreateDirectory(directory);
            WritePrivate(path, rsa.ExportPkcs8PrivateKeyPem());
        }

        var key = new RsaSecurityKey(rsa);
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        return key;
    }

    // Written to a temp file and moved into place, so a crash never leaves a half-written key behind.
    private static void WritePrivate(string path, string pem)
    {
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 0600

        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(pem);

        File.Move(temp, path, overwrite: false);
    }
}
