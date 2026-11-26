using Microsoft.EntityFrameworkCore;

namespace TunarrMcp.Server.Auth;

/// <summary>
/// OpenIddict's applications, authorizations, scopes and tokens. Nothing from Tunarr is stored here.
/// The schema is created with EnsureCreated rather than migrations: the data is disposable (deleting the file
/// costs one fresh sign-in, since clients re-register on their own), so migrations would be ceremony.
/// </summary>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options);
