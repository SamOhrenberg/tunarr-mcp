using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Server.Hosting;

internal static class McpServerRegistration
{
    /// <summary>
    /// Tool results are JSON text read by a model, never embedded in HTML. The default encoder escapes ' + &amp; and
    /// non-ASCII (Grey's Anatomy, Pokémon), which costs tokens and hurts readability, so relax it.
    /// </summary>
    private static readonly JsonSerializerOptions ToolJson = new(McpJsonUtilities.DefaultOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IServiceCollection AddTunarrMcpServer(this IServiceCollection services, IConfiguration configuration)
    {
        // Needed at registration time, before options are built. Invalid values throw here, at startup.
        var mode = configuration.GetSection(ToolsOptions.Section).Get<ToolsOptions>()?.Mode ?? ToolsMode.Read;

        services.TryAddSingleton(TimeProvider.System);

        services.AddMcpServer(o => o.ServerInfo = new Implementation
            {
                Name = "tunarr-mcp",
                Title = "Tunarr",
                Version = typeof(McpServerRegistration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
            })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(CreateTools(mode))
            .WithRequestFilters(f => f.AddCallToolFilter(TunarrErrorFilter.Create));

        return services;
    }

    /// <summary>
    /// Every [McpServerTool] method in this assembly whose annotations the mode allows.
    /// Gating is per tool, from its MCP annotations, so a class can mix read and write tools and
    /// a tool that forgets its annotations is treated as destructive (the MCP default) and stays hidden.
    /// </summary>
    internal static IEnumerable<McpServerTool> CreateTools(ToolsMode mode) =>
        from type in typeof(McpServerRegistration).Assembly.GetTypes()
        where type.IsDefined(typeof(McpServerToolTypeAttribute))
        from method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        where method.IsDefined(typeof(McpServerToolAttribute))
        let tool = McpServerTool.Create(method, CreateTarget(type), new McpServerToolCreateOptions { SerializerOptions = ToolJson })
        where IsAllowed(tool.ProtocolTool.Annotations, mode)
        select tool;

    internal static bool IsAllowed(ToolAnnotations? annotations, ToolsMode mode) => mode switch
    {
        ToolsMode.Read => annotations?.ReadOnlyHint == true,
        ToolsMode.Write => annotations?.ReadOnlyHint == true || annotations?.DestructiveHint == false,
        ToolsMode.All => true,
        _ => false,
    };

    // A fresh instance per call, with constructor dependencies from the request's service scope.
    private static Func<RequestContext<CallToolRequestParams>, object> CreateTarget(Type type) =>
        context => ActivatorUtilities.CreateInstance(context.Services!, type);
}
