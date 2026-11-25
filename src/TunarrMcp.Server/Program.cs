using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using TunarrMcp.Server.Configuration;
using TunarrMcp.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddSecretFiles();
builder.Services.AddTunarrMcpOptions();
builder.Services.AddTunarrClient();

var app = builder.Build();

// Liveness runs no checks; readiness also checks that Tunarr's API answers.
app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(TunarrHealthCheck.ReadyTag) });

app.Run();

// Exposed so WebApplicationFactory<Program> can host the app in tests.
public partial class Program;
