using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TunarrMcp.Server.Configuration;

namespace TunarrMcp.Server.Changes;

/// <summary>
/// The undo history: one <see cref="ChannelChange"/> per write, newest last. Writes go through
/// <see cref="RunExclusiveAsync{T}"/> so a snapshot, the write and its record can't interleave with another write.
/// </summary>
public sealed class ChangeLog(IDbContextFactory<ChangeDbContext> contexts, TimeProvider clock) : IDisposable
{
    public const string DatabaseFile = "changes.db";

    /// <summary>Changes kept per channel; older ones can no longer be undone.</summary>
    public const int KeptPerChannel = 20;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _created;

    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> write, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await write().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ChannelChange> RecordAsync(ChannelChange change, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        change.MadeAt = clock.GetUtcNow();
        db.Changes.Add(change);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var stale = await db.Changes
            .Where(c => c.ChannelId == change.ChannelId)
            .OrderByDescending(c => c.Id)
            .Skip(KeptPerChannel)
            .ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count > 0)
        {
            db.Changes.RemoveRange(stale);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return change;
    }

    /// <summary>The newest change not yet undone, on one channel or on any.</summary>
    public async Task<ChannelChange?> LatestAsync(string? channelId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        return await Undoable(db, channelId).OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountUndoableAsync(string channelId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        return await Undoable(db, channelId).CountAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkUndoneAsync(long id, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        var change = await db.Changes.SingleAsync(c => c.Id == id, ct).ConfigureAwait(false);
        change.UndoneAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => _writeLock.Dispose();

    private static IQueryable<ChannelChange> Undoable(ChangeDbContext db, string? channelId) =>
        db.Changes.Where(c => c.UndoneAt == null && (channelId == null || c.ChannelId == channelId));

    private async Task<ChangeDbContext> OpenAsync(CancellationToken ct)
    {
        var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!_created)
        {
            // The folder and file appear on first use, not at startup or when a tool is built: read-only setups and
            // read tools never touch the disk.
            if (Path.GetDirectoryName(db.Database.GetDbConnection().DataSource) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);
            await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            _created = true;
        }

        return db;
    }
}

internal static class ChangeLogRegistration
{
    public static IServiceCollection AddChangeLog(this IServiceCollection services)
    {
        services.AddDbContextFactory<ChangeDbContext>((sp, db) =>
        {
            var data = sp.GetRequiredService<IOptions<DataOptions>>().Value;
            db.UseSqlite($"Data Source={Path.Combine(data.Path, ChangeLog.DatabaseFile)}");
        });
        services.AddSingleton<ChangeLog>();
        return services;
    }
}
