using Microsoft.EntityFrameworkCore;

namespace TunarrMcp.Server.Changes;

/// <summary>
/// Snapshots taken before each write, for undo_last_change. Its own SQLite file next to the auth database, created
/// on the first write, so read-only setups never get one. EnsureCreated rather than migrations, as for auth: losing
/// the file only loses the undo history. New fields go in <see cref="ChannelState"/>'s JSON, not new columns,
/// so an existing file keeps working.
/// </summary>
public sealed class ChangeDbContext(DbContextOptions<ChangeDbContext> options) : DbContext(options)
{
    public DbSet<ChannelChange> Changes => Set<ChannelChange>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ChannelChange>().HasIndex(c => c.ChannelId);
}

/// <summary>One write a tool made to a channel, with the channel as it was before.</summary>
public sealed class ChannelChange
{
    public long Id { get; set; }

    public required string ChannelId { get; set; }

    public int ChannelNumber { get; set; }

    public required string ChannelName { get; set; }

    /// <summary>The tool that made the change.</summary>
    public required string Tool { get; set; }

    /// <summary>What it did, in a sentence.</summary>
    public required string Summary { get; set; }

    public DateTimeOffset MadeAt { get; set; }

    /// <summary>JSON of <see cref="ChannelState"/>: what undo restores.</summary>
    public required string Before { get; set; }

    /// <summary><see cref="LineupFingerprint"/> of the lineup the change left, to notice later edits.</summary>
    public required string AfterFingerprint { get; set; }

    public DateTimeOffset? UndoneAt { get; set; }
}
