using TunarrMcp.Server.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddSecretFiles();
builder.Services.AddTunarrMcpOptions();

var app = builder.Build();

// Liveness only. Readiness (which also checks Tunarr) is added with the Tunarr client.
app.MapGet("/healthz", () => Results.Ok());

app.Run();

// Exposed so WebApplicationFactory<Program> can host the app in tests.
public partial class Program;
