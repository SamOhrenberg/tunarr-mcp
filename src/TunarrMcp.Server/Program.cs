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

// Embedded mode: OAuth store, auth middleware, /login, /connect/authorize and /connect/token.
await app.UseTunarrMcpAuthAsync();

// Not yet authenticated: Auth:Mode=None only. OAuth protection lands before this is exposed.
app.MapMcp(app.Services.GetRequiredService<IOptions<McpOptions>>().Value.Path);

await app.RunAsync();

// Exposed so WebApplicationFactory<Program> can host the app in tests.
public partial class Program;
