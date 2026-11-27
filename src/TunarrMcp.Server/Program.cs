using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TunarrMcp.Server.Auth;
using TunarrMcp.Server.Configuration;
using TunarrMcp.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddSecretFiles();
builder.Services.AddTunarrMcpOptions();
builder.Services.AddTunarrClient();
builder.Services.AddTunarrMcpServer(builder.Configuration);
builder.Services.AddTunarrMcpAuth(builder.Configuration);

var app = builder.Build();

// Liveness runs no checks; readiness also checks that Tunarr's API answers.
app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(TunarrHealthCheck.ReadyTag) });

// Origin check and optional IP allowlist on /mcp, in every auth mode.
app.UseMcpRequestGuard();

// Embedded mode: OAuth store, auth middleware, /login, /connect/*, /register, protected-resource metadata.
await app.UseTunarrMcpAuthAsync();

app.MapMcp(app.Services.GetRequiredService<IOptions<McpOptions>>().Value.Path)
    .RequireMcpAuthorization(app.Configuration);

await app.RunAsync();

// Exposed so WebApplicationFactory<Program> can host the app in tests.
public partial class Program;
