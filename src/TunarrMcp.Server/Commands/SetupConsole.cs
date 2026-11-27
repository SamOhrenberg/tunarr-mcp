using System.Text;

namespace TunarrMcp.Server.Commands;

/// <summary>The terminal, behind an interface so the setup flow can be tested without one.</summary>
internal interface ISetupConsole
{
    /// <summary>False when input is piped, so prompts that need a person (confirmation codes) are skipped.</summary>
    bool IsInteractive { get; }

    /// <summary>Reads a line without echoing it.</summary>
    string ReadSecret(string prompt);

    string? ReadLine(string prompt);

    void Write(string text);
}

internal sealed class SystemConsole : ISetupConsole
{
    public SystemConsole() => Console.OutputEncoding = Encoding.UTF8; // the QR code uses block characters

    public bool IsInteractive => !Console.IsInputRedirected;

    public string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                    value.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return value.ToString();
    }

    public string? ReadLine(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine();
    }

    public void Write(string text) => Console.Write(text);
}
