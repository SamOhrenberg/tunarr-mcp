using System.Text;
using Microsoft.AspNetCore.Identity;
using OtpNet;
using QRCoder;

namespace TunarrMcp.Server.Commands;

/// <summary>
/// <c>tunarr-mcp setup [--out DIR] [--account NAME]</c>: creates the admin credentials for Embedded mode, so nobody
/// needs the .NET SDK. Asks for a password, generates a TOTP secret, shows it as a QR code, and (when run
/// interactively) asks for one code from the authenticator before writing anything, so a mistyped secret
/// is caught now rather than at the first login.
/// </summary>
internal static class SetupCommand
{
    public const string PasswordHashFile = "admin_password_hash";
    public const string TotpSecretFile = "totp_secret";
    public const int MinPasswordLength = 12;
    private const string Issuer = "Tunarr MCP";
    private const int Attempts = 3;

    public static int Run(string[] args, ISetupConsole console)
    {
        if (!TryParse(args, out var outDir, out var account))
        {
            console.Write("""
                Usage: setup [--out DIR] [--account NAME]

                  --out DIR       Where to write admin_password_hash and totp_secret (default: ./secrets)
                  --account NAME  Label shown in the authenticator app (default: admin)

                In Docker:  docker run --rm -it -v /path/on/host:/secrets IMAGE setup --out /secrets
                From source (dotnet run starts in the project folder, so the path is relative to it):
                            dotnet run --project src/TunarrMcp.Server -- setup --out ../../.data/secrets

                """);
            return 2;
        }

        var hashPath = Path.Combine(outDir, PasswordHashFile);
        var secretPath = Path.Combine(outDir, TotpSecretFile);
        if (File.Exists(hashPath) || File.Exists(secretPath))
        {
            console.Write($"{outDir} already has credentials. Delete {PasswordHashFile} and {TotpSecretFile} to start over.\n");
            return 1;
        }

        if (ReadPassword(console) is not { } password)
            return 1;

        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20)); // 160 bits, RFC 4226's recommendation
        var uri = OtpAuthUri(account, secret);

        console.Write($"""

            Scan this with your authenticator app (Google Authenticator, 1Password, Aegis, ...):

            {QrCode(uri)}
            Or enter the key by hand: {secret}
            Or paste this link into a password manager: {uri}

            """);

        if (console.IsInteractive && !ConfirmCode(console, secret))
            return 1;

        Directory.CreateDirectory(outDir);
        WriteSecret(hashPath, new PasswordHasher<object>().HashPassword(new object(), password));
        WriteSecret(secretPath, secret);

        console.Write($"""

            Wrote {hashPath}
                  {secretPath}

            Point the server at them, for example with Docker secrets:

              Auth__AdminUser={account}
              Auth__AdminPasswordHashFile=/run/secrets/{PasswordHashFile}
              Auth__TotpSecretFile=/run/secrets/{TotpSecretFile}

            """);
        return 0;
    }

    public static string OtpAuthUri(string account, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={secret}&issuer={Uri.EscapeDataString(Issuer)}&algorithm=SHA1&digits=6&period=30";

    private static string? ReadPassword(ISetupConsole console)
    {
        if (!console.IsInteractive)
        {
            // Piped input (scripts): one line, no confirmation prompt.
            var piped = console.ReadLine("");
            if (piped is { Length: >= MinPasswordLength })
                return piped;
            console.Write($"The password must be at least {MinPasswordLength} characters.\n");
            return null;
        }

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var password = console.ReadSecret($"Admin password (at least {MinPasswordLength} characters): ");
            if (password.Length < MinPasswordLength)
            {
                console.Write("Too short.\n");
                continue;
            }

            if (console.ReadSecret("Repeat it: ") == password)
                return password;
            console.Write("They don't match.\n");
        }

        return null;
    }

    private static bool ConfirmCode(ISetupConsole console, string secret)
    {
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var code = console.ReadLine("Enter the 6-digit code your app shows, to confirm: ")?.Replace(" ", "", StringComparison.Ordinal);
            if (code is not null && totp.VerifyTotp(code, out _, VerificationWindow.RfcSpecifiedNetworkDelay))
                return true;
            console.Write("That code doesn't match. Check the app's clock and the scanned entry.\n");
        }

        console.Write("Nothing was written. Remove the entry from your app and run setup again.\n");
        return false;
    }

    private static string QrCode(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new AsciiQRCode(data).GetGraphicSmall(drawQuietZones: true, invert: false, endOfLine: "\n");
    }

    // CreateNew: never overwrite. 0600 on Linux, since these are secrets.
    private static void WriteSecret(string path, string contents)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        stream.Write(Encoding.UTF8.GetBytes(contents));
    }

    private static bool TryParse(string[] args, out string outDir, out string account)
    {
        outDir = Path.GetFullPath("secrets");
        account = "admin";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outDir = Path.GetFullPath(args[++i]);
                    break;
                case "--account" when i + 1 < args.Length:
                    account = args[++i];
                    break;
                default:
                    return false; // includes --help
            }
        }

        return true;
    }
}
