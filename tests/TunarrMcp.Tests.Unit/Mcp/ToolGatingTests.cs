using ModelContextProtocol.Protocol;
using TunarrMcp.Server.Configuration;
using TunarrMcp.Server.Hosting;

namespace TunarrMcp.Tests.Unit.Mcp;

public sealed class ToolGatingTests
{
    [Theory]
    //          readOnly  destructive  read   write  all
    [InlineData(true,     null,        true,  true,  true)]
    [InlineData(false,    false,       false, true,  true)]
    [InlineData(false,    true,        false, false, true)]
    [InlineData(false,    null,        false, false, true)] // destructiveHint unset defaults to true in MCP
    [InlineData(null,     null,        false, false, true)] // no annotations at all
    public void Mode_allows_tools_by_annotation(bool? readOnly, bool? destructive, bool inRead, bool inWrite, bool inAll)
    {
        var annotations = new ToolAnnotations { ReadOnlyHint = readOnly, DestructiveHint = destructive };

        Assert.Equal(inRead, McpServerRegistration.IsAllowed(annotations, ToolsMode.Read));
        Assert.Equal(inWrite, McpServerRegistration.IsAllowed(annotations, ToolsMode.Write));
        Assert.Equal(inAll, McpServerRegistration.IsAllowed(annotations, ToolsMode.All));
    }

    [Fact]
    public void Read_mode_registers_only_read_only_tools()
    {
        var tools = McpServerRegistration.CreateTools(ToolsMode.Read).ToArray();

        Assert.NotEmpty(tools);
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint, t.ProtocolTool.Name));
    }

    [Fact]
    public void Write_mode_registers_no_destructive_tools()
    {
        var tools = McpServerRegistration.CreateTools(ToolsMode.Write);

        Assert.DoesNotContain(tools, t =>
            t.ProtocolTool.Annotations?.ReadOnlyHint != true && t.ProtocolTool.Annotations?.DestructiveHint != false);
    }

    [Fact]
    public void Tool_names_are_unique_and_snake_case()
    {
        var names = McpServerRegistration.CreateTools(ToolsMode.All).Select(t => t.ProtocolTool.Name).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, n => Assert.Matches("^[a-z][a-z0-9_]*$", n));
    }
}
