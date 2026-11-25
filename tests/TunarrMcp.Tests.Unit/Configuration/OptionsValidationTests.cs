using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Tests.Unit.Configuration;

public sealed class OptionsValidationTests
{
    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["Mcp:PublicUrl"] = "https://mcp.example.com/mcp",
        ["Auth:Mode"] = "None",
        ["Auth:AllowInsecureNone"] = "true",
    };

    private static T Get<T>(Dictionary<string, string?>? overrides = null) where T : class
    {
        var settings = new Dictionary<string, string?>(Valid);
        foreach (var (key, value) in overrides ?? [])
            settings[key] = value;

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .AddTunarrMcpOptions();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<T>>().Value;
    }

    private static string Fails<T>(Dictionary<string, string?> overrides) where T : class =>
        Assert.Throws<OptionsValidationException>(() => Get<T>(overrides)).Message;

    [Fact]
    public void Minimal_config_is_valid_and_uses_safe_defaults()
    {
        Assert.Equal(ToolsMode.Read, Get<ToolsOptions>().Mode);
        Assert.Equal(new Uri("http://tunarr:8000/api/"), Get<TunarrOptions>().BaseUrl);
        Assert.Equal("/data", Get<DataOptions>().Path);
    }

    [Fact]
    public void Issuer_and_path_derive_from_public_url()
    {
        var mcp = Get<McpOptions>();

        Assert.Equal(new Uri("https://mcp.example.com/mcp"), mcp.Resource);
        Assert.Equal(new Uri("https://mcp.example.com/"), mcp.Issuer);
        Assert.Equal("/mcp", mcp.Path);
    }

    [Fact]
    public void Public_url_is_required()
    {
        var message = Fails<McpOptions>(new() { ["Mcp:PublicUrl"] = null });

        Assert.Contains("Mcp:PublicUrl is required", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://mcp.example.com/mcp", "must use https")]
    [InlineData("https://mcp.example.com/", "endpoint path")]
    [InlineData("https://mcp.example.com/mcp/", "endpoint path")]
    [InlineData("https://mcp.example.com/mcp?x=1", "query string")]
    public void Public_url_rejects_unsafe_or_ambiguous_values(string url, string expected)
    {
        var message = Fails<McpOptions>(new() { ["Mcp:PublicUrl"] = url });

        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_url_allows_http_on_localhost()
    {
        Assert.Equal("/mcp", Get<McpOptions>(new() { ["Mcp:PublicUrl"] = "http://localhost:8080/mcp" }).Path);
    }

    [Fact]
    public void Tunarr_base_url_gets_a_trailing_slash()
    {
        var tunarr = Get<TunarrOptions>(new() { ["Tunarr:BaseUrl"] = "http://192.0.2.10:8001/api" });

        Assert.Equal(new Uri("http://192.0.2.10:8001/api/"), tunarr.BaseUrl);
    }

    [Fact]
    public void Tunarr_basic_auth_needs_user_and_password_together()
    {
        var message = Fails<TunarrOptions>(new() { ["Tunarr:BasicAuthUser"] = "admin" });

        Assert.Contains("Tunarr:BasicAuthPassword", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Auth_none_requires_explicit_opt_in()
    {
        var message = Fails<AuthOptions>(new() { ["Auth:AllowInsecureNone"] = null });

        Assert.Contains("AllowInsecureNone=true", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Auth_defaults_to_embedded_and_lists_every_missing_secret()
    {
        var message = Fails<AuthOptions>(new() { ["Auth:Mode"] = null, ["Auth:AllowInsecureNone"] = null });

        Assert.Contains("Auth:AdminUser", message, StringComparison.Ordinal);
        Assert.Contains("Auth:AdminPasswordHash", message, StringComparison.Ordinal);
        Assert.Contains("Auth:TotpSecret", message, StringComparison.Ordinal);
    }

    [Fact]
    public void External_oidc_requires_https_authority()
    {
        var message = Fails<AuthOptions>(new() { ["Auth:Mode"] = "ExternalOidc", ["Auth:Authority"] = "http://idp.example.com" });

        Assert.Contains("Auth:Authority", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Allowed_ip_ranges_are_parsed_and_validated()
    {
        var auth = Get<AuthOptions>(new() { ["Auth:AllowedIpRanges"] = "160.79.104.0/21, 203.0.113.7/32" });
        Assert.Equal(2, auth.ParseAllowedIpRanges().Count);

        var message = Fails<AuthOptions>(new() { ["Auth:AllowedIpRanges"] = "160.79.104.0/21,not-a-range" });
        Assert.Contains("'not-a-range'", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tools_mode_is_case_insensitive()
    {
        Assert.Equal(ToolsMode.Write, Get<ToolsOptions>(new() { ["Tools:Mode"] = "write" }).Mode);
    }
}
