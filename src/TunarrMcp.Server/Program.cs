using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TunarrMcp.Server.Auth;
using TunarrMcp.Server.Commands;
using TunarrMcp.Server.Configuration;
using TunarrMcp.Server.Hosting;

// One-shot commands that run instead of the server.
switch (args)
{
    case ["setup", .. var setupArgs]:
        return SetupCommand.Run(setupArgs, new SystemConsole());
    case ["--healthcheck", .. var healthArgs]:
        return await HealthcheckCommand.RunAsync(healthArgs);
}

WebApplication app;
try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Configuration.AddSecretFiles(builder.Environment.ContentRootPath);
    builder.Services.AddTunarrMcpOptions();
    builder.Services.AddTunarrClient();
    builder.Services.AddTunarrMcpServer(builder.Configuration);
    builder.Services.AddTunarrMcpAuth(builder.Configuration);

    app = builder.Build();

    // Validate every options section now, so all mistakes are reported together, before the
    // pipeline below reads individual options.
    app.Services.GetService<IStartupValidator>()?.Validate();

    // Liveness runs no checks; readiness also checks that Tunarr's API answers.
    app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(TunarrHealthCheck.ReadyTag) });

    // Origin check and optional IP allowlist on /mcp, in every auth mode.
    app.UseMcpRequestGuard();

    // Embedded mode: OAuth store, auth middleware, /login, /connect/*, /register, protected-resource metadata.
    await app.UseTunarrMcpAuthAsync();

    app.MapMcp(app.Services.GetRequiredService<IOptions<McpOptions>>().Value.Path)
        .RequireMcpAuthorization(app.Configuration);

    await app.StartAsync();
}
catch (Exception e) when (StartupErrors.Describe(e) is { } message)
{
    // A configuration mistake: say what to fix, without a stack trace.
    await Console.Error.WriteLineAsync(message);
    return 1;
}

await app.WaitForShutdownAsync();
await app.DisposeAsync();
return 0;

// Exposed so WebApplicationFactory<Program> can host the app in tests.
public partial class Program;
