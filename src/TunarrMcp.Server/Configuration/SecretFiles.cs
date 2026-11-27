namespace TunarrMcp.Server.Configuration;

/// <summary>
/// Lets every secret be supplied as a file (Docker secrets) via a sibling <c>*File</c> key,
/// for example <c>Auth__TotpSecretFile=/run/secrets/totp_secret</c>.
/// Only the keys listed here are read from disk, so config can't be used to read arbitrary files.
/// </summary>
internal static class SecretFiles
{
    internal static readonly string[] Keys =
    [
        "Tunarr:BasicAuthPassword",
        "Auth:AdminPasswordHash",
        "Auth:TotpSecret",
    ];

    /// <summary>
    /// Reads each configured secret file and layers its contents over the existing configuration.
    /// Relative paths resolve against <paramref name="basePath"/> (the content root), like Data:Path.
    /// </summary>
    public static ConfigurationManager AddSecretFiles(this ConfigurationManager configuration, string basePath)
    {
        configuration.AddInMemoryCollection(Resolve(configuration, basePath));
        return configuration;
    }

    internal static Dictionary<string, string?> Resolve(IConfiguration configuration, string basePath)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in Keys)
        {
            var configured = configuration[key + "File"];
            if (string.IsNullOrEmpty(configured))
                continue;
            var path = Path.GetFullPath(configured, basePath);

            if (!string.IsNullOrEmpty(configuration[key]))
                throw new InvalidOperationException($"Set either {key} or {key}File, not both.");

            string contents;
            try
            {
                contents = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The path is not secret, the contents are. Never include the contents in the message.
                throw new InvalidOperationException($"{key}File points to '{path}', which could not be read: {e.Message}", e);
            }

            // Files written by `echo` or editors usually end in a newline that isn't part of the secret.
            values[key] = contents.TrimEnd('\r', '\n');
        }

        return values;
    }
}
