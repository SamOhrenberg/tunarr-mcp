using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using OtpNet;
using TunarrMcp.Server.Commands;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Tests.Unit.Commands;

public sealed partial class SetupCommandTests : IDisposable
{
    private const string Password = "a long enough password";
    private readonly string _dir = Path.Combine(Directory.CreateTempSubdirectory("tunarr-mcp-setup-").FullName, "secrets");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);

    private string HashPath => Path.Combine(_dir, SetupCommand.PasswordHashFile);
    private string SecretPath => Path.Combine(_dir, SetupCommand.TotpSecretFile);

    private int Run(ScriptedConsole console, params string[] extraArgs) =>
        SetupCommand.Run(["--out", _dir, .. extraArgs], console);

    /// <summary>What a phone would show: the current code for the key setup printed.</summary>
    private static string CodeFromOutput(string output) =>
        new Totp(Base32Encoding.ToBytes(ManualKey().Match(output).Groups[1].Value)).ComputeTotp(DateTime.UtcNow);

    [Fact]
    public void Interactive_setup_writes_credentials_the_server_accepts()
    {
        var console = new ScriptedConsole(interactive: true, _ => Password, _ => Password, CodeFromOutput);

        Assert.Equal(0, Run(console, "--account", "sam"));

        var hash = File.ReadAllText(HashPath);
        var secret = File.ReadAllText(SecretPath);
        Assert.True(AdminCredentialFormats.IsPasswordHash(hash));
        Assert.True(AdminCredentialFormats.IsTotpSecret(secret));
        Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<object>().VerifyHashedPassword(new object(), hash, Password));
        Assert.Contains($"otpauth://totp/Tunarr%20MCP:sam?secret={secret}&issuer=Tunarr%20MCP", console.Output, StringComparison.Ordinal);
        Assert.Contains("▀", console.Output, StringComparison.Ordinal); // the QR code
        Assert.Contains("Auth__AdminUser=sam", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Short_or_mismatched_passwords_are_asked_again()
    {
        var console = new ScriptedConsole(interactive: true,
            _ => "short",
            _ => Password, _ => "typo " + Password,
            _ => Password, _ => Password,
            CodeFromOutput);

        Assert.Equal(0, Run(console));
        Assert.Contains("Too short.", console.Output, StringComparison.Ordinal);
        Assert.Contains("They don't match.", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Three_failed_password_attempts_write_nothing()
    {
        var console = new ScriptedConsole(interactive: true, _ => "short", _ => "short", _ => "short");

        Assert.Equal(1, Run(console));
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Wrong_authenticator_codes_write_nothing()
    {
        var console = new ScriptedConsole(interactive: true, _ => Password, _ => Password, _ => "000000", _ => "000000", _ => "000000");

        Assert.Equal(1, Run(console));
        Assert.False(File.Exists(HashPath));
        Assert.False(File.Exists(SecretPath));
        Assert.Contains("Nothing was written", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_credentials_are_never_overwritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SecretPath, "keep me");

        Assert.Equal(1, Run(new ScriptedConsole(interactive: true)));
        Assert.Equal("keep me", File.ReadAllText(SecretPath));
    }

    [Fact]
    public void Piped_password_is_accepted_without_confirmation()
    {
        Assert.Equal(0, Run(new ScriptedConsole(interactive: false, _ => Password)));
        Assert.True(File.Exists(HashPath));
    }

    [Fact]
    public void Unknown_arguments_print_usage()
    {
        var console = new ScriptedConsole(interactive: true);

        Assert.Equal(2, SetupCommand.Run(["--help"], console));
        Assert.Contains("Usage: setup", console.Output, StringComparison.Ordinal);
    }

    [GeneratedRegex("enter the key by hand: ([A-Z2-7]+)")]
    private static partial Regex ManualKey();

    /// <summary>Answers each prompt in order; each answer can look at everything printed so far.</summary>
    private sealed class ScriptedConsole(bool interactive, params Func<string, string>[] answers) : ISetupConsole
    {
        private readonly Queue<Func<string, string>> _answers = new(answers);
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public bool IsInteractive => interactive;

        public string ReadSecret(string prompt) => ReadLine(prompt) ?? "";

        public string? ReadLine(string prompt)
        {
            _output.Append(prompt);
            return _answers.TryDequeue(out var answer) ? answer(Output) : null;
        }

        public void Write(string text) => _output.Append(text);
    }
}
