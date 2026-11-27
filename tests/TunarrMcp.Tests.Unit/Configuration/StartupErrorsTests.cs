using Microsoft.Extensions.Options;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Tests.Unit.Configuration;

public sealed class StartupErrorsTests
{
    [Fact]
    public void Configuration_exception_is_shown_as_its_message()
    {
        Assert.Equal("Configuration error:\n  - Run setup.", StartupErrors.Describe(new ConfigurationException("Run setup.")));
    }

    [Fact]
    public void Every_failure_from_ValidateOnStart_is_listed()
    {
        var aggregate = new AggregateException(
            new OptionsValidationException("Mcp", typeof(McpOptions), ["Mcp:PublicUrl is required."]),
            new OptionsValidationException("Auth", typeof(AuthOptions), ["Auth:AdminUser is required.", "Auth:TotpSecret is required."]));

        Assert.Equal(
            "Configuration error:\n  - Mcp:PublicUrl is required.\n  - Auth:AdminUser is required.\n  - Auth:TotpSecret is required.",
            StartupErrors.Describe(aggregate));
    }

    [Fact]
    public void Anything_else_keeps_its_stack_trace()
    {
        Assert.Null(StartupErrors.Describe(new InvalidOperationException("a bug")));
        Assert.Null(StartupErrors.Describe(new AggregateException(new ConfigurationException("x"), new IOException())));
    }
}
