using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Options;

namespace TunarrMcp.Server.Configuration;

// Startup validation. Messages name the exact config key so a misconfigured container says what to fix.

internal sealed class McpOptionsValidator : IValidateOptions<McpOptions>
{
    public ValidateOptionsResult Validate(string? name, McpOptions options)
    {
        const string key = $"{McpOptions.Section}:{nameof(McpOptions.PublicUrl)}";
        var url = options.PublicUrl;

        if (url is null)
            return ValidateOptionsResult.Fail($"{key} is required, for example https://mcp.example.com/mcp.");
        if (!url.IsAbsoluteUri)
            return ValidateOptionsResult.Fail($"{key} must be an absolute URL.");

        var result = new ValidateOptionsResultBuilder();
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            result.AddError($"{key} must use https. Plain http is allowed only for localhost.");
        if (url.Query.Length > 0 || url.Fragment.Length > 0)
            result.AddError($"{key} must not contain a query string or fragment.");
        if (url.AbsolutePath == "/" || url.AbsolutePath.EndsWith('/'))
            result.AddError($"{key} must end in the endpoint path without a trailing slash, for example /mcp.");
        return result.Build();
    }
}

internal sealed class TunarrOptionsValidator : IValidateOptions<TunarrOptions>
{
    public ValidateOptionsResult Validate(string? name, TunarrOptions options)
    {
        var result = new ValidateOptionsResultBuilder();
        if (!options.BaseUrl.IsAbsoluteUri || options.BaseUrl.Scheme is not ("http" or "https"))
            result.AddError($"{TunarrOptions.Section}:{nameof(TunarrOptions.BaseUrl)} must be an absolute http(s) URL.");

        var hasUser = !string.IsNullOrEmpty(options.BasicAuthUser);
        var hasPassword = !string.IsNullOrEmpty(options.BasicAuthPassword);
        if (hasUser != hasPassword)
            result.AddError($"Set both {TunarrOptions.Section}:{nameof(TunarrOptions.BasicAuthUser)} and " +
                            $"{TunarrOptions.Section}:{nameof(TunarrOptions.BasicAuthPassword)}(File), or neither.");
        return result.Build();
    }
}

internal sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    private const string S = AuthOptions.Section;

    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var result = new ValidateOptionsResultBuilder();

        switch (options.Mode)
        {
            case AuthMode.Embedded:
                Require(result, options.AdminUser, $"{S}:{nameof(AuthOptions.AdminUser)}", fromSetup: false);
                if (Require(result, options.AdminPasswordHash, $"{S}:{nameof(AuthOptions.AdminPasswordHash)}(File)", fromSetup: true)
                    && !AdminCredentialFormats.IsPasswordHash(options.AdminPasswordHash))
                    result.AddError($"{S}:{nameof(AuthOptions.AdminPasswordHash)} is not a password hash. Run the 'setup' command to generate one.");
                if (Require(result, options.TotpSecret, $"{S}:{nameof(AuthOptions.TotpSecret)}(File)", fromSetup: true)
                    && !AdminCredentialFormats.IsTotpSecret(options.TotpSecret))
                    result.AddError($"{S}:{nameof(AuthOptions.TotpSecret)} must be base32 and at least {AdminCredentialFormats.MinTotpSecretBytes * 8} bits. Run the 'setup' command to generate one.");
                break;

            case AuthMode.ExternalOidc:
                if (options.Authority is not { IsAbsoluteUri: true, Scheme: "https" })
                    result.AddError($"{S}:{nameof(AuthOptions.Authority)} must be an absolute https URL in ExternalOidc mode.");
                break;

            case AuthMode.None when !options.AllowInsecureNone:
                result.AddError($"{S}:Mode=None turns off authentication on the MCP endpoint. " +
                                $"Set {S}:{nameof(AuthOptions.AllowInsecureNone)}=true to confirm. Use it only on a LAN or for local testing.");
                break;
        }

        foreach (var entry in AuthOptions.SplitList(options.AllowedRedirectUris))
        {
            if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri)
                || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
                || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                result.AddError($"{S}:{nameof(AuthOptions.AllowedRedirectUris)} entry '{entry}' must be an absolute https URL " +
                                "(http only for localhost), without a fragment or credentials.");
        }

        foreach (var range in AuthOptions.SplitList(options.AllowedIpRanges))
        {
            if (!IPNetwork.TryParse(range, out _))
                result.AddError($"{S}:{nameof(AuthOptions.AllowedIpRanges)} entry '{range}' is not a valid CIDR range.");
        }

        return result.Build();
    }

    private static bool Require(ValidateOptionsResultBuilder result, [NotNullWhen(true)] string? value, string key, bool fromSetup)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return true;

        result.AddError($"{key} is required in Embedded mode." + (fromSetup ? " Run the 'setup' command to generate it." : ""));
        return false;
    }
}

internal static class AdminCredentialFormats
{
    public const int MinTotpSecretBytes = 16;

    /// <summary>ASP.NET Core Identity's PasswordHasher v3 format: base64, first byte 0x01.</summary>
    public static bool IsPasswordHash(string value)
    {
        var bytes = new byte[value.Length];
        return Convert.TryFromBase64String(value, bytes, out var length) && length > 13 && bytes[0] == 0x01;
    }

    public static bool IsTotpSecret(string value)
    {
        try
        {
            return OtpNet.Base32Encoding.ToBytes(value).Length >= MinTotpSecretBytes;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

internal sealed class DataOptionsValidator : IValidateOptions<DataOptions>
{
    public ValidateOptionsResult Validate(string? name, DataOptions options) =>
        string.IsNullOrWhiteSpace(options.Path)
            ? ValidateOptionsResult.Fail($"{DataOptions.Section}:{nameof(DataOptions.Path)} is required.")
            : ValidateOptionsResult.Success;
}
