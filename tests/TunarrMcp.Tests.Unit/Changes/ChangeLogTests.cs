using Microsoft.EntityFrameworkCore;
using TunarrMcp.Server.Changes;
using TunarrMcp.Tunarr.Models;

namespace TunarrMcp.Tests.Unit.Changes;

public sealed class ChangeLogTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("tunarr-mcp-changelog-").FullName;
    private readonly ChangeLog _log;

    public ChangeLogTests()
    {
        var options = new DbContextOptionsBuilder<ChangeDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_data, ChangeLog.DatabaseFile)}").Options;
        _log = new ChangeLog(new Factory(options), TimeProvider.System);
    }

    public void Dispose()
    {
        _log.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_data, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<ChannelChange> Record(string channel, string summary) => _log.RecordAsync(new ChannelChange
    {
        ChannelId = channel, ChannelNumber = 1, ChannelName = "Ch", Tool = "add_to_channel", Summary = summary,
        Before = "{}", AfterFingerprint = "x",
    }, Ct);

    [Fact]
    public async Task Latest_is_the_newest_change_not_undone_per_channel_or_overall()
    {
        await Record("a", "first");
        await Record("b", "other channel");
        var second = await Record("a", "second");

        Assert.Equal("second", (await _log.LatestAsync("a", Ct))?.Summary);
        Assert.Equal("second", (await _log.LatestAsync(null, Ct))?.Summary);

        await _log.MarkUndoneAsync(second.Id, Ct);

        Assert.Equal("first", (await _log.LatestAsync("a", Ct))?.Summary);
        Assert.Equal("other channel", (await _log.LatestAsync(null, Ct))?.Summary);
        Assert.Equal(1, await _log.CountUndoableAsync("a", Ct));
    }

    [Fact]
    public async Task Only_the_newest_changes_per_channel_are_kept()
    {
        for (var i = 0; i < ChangeLog.KeptPerChannel + 5; i++)
            await Record("a", $"change {i}");
        await Record("b", "untouched");

        Assert.Equal(ChangeLog.KeptPerChannel, await _log.CountUndoableAsync("a", Ct));
        Assert.Equal(1, await _log.CountUndoableAsync("b", Ct));
        Assert.Equal($"change {ChangeLog.KeptPerChannel + 4}", (await _log.LatestAsync("a", Ct))?.Summary);
    }

    [Fact]
    public async Task Database_folder_is_created_on_first_use()
    {
        var folder = Path.Combine(_data, "not", "yet");
        var options = new DbContextOptionsBuilder<ChangeDbContext>()
            .UseSqlite($"Data Source={Path.Combine(folder, ChangeLog.DatabaseFile)}").Options;
        using var log = new ChangeLog(new Factory(options), TimeProvider.System);
        Assert.False(Directory.Exists(folder));

        Assert.Null(await log.LatestAsync(null, Ct));

        Assert.True(File.Exists(Path.Combine(folder, ChangeLog.DatabaseFile)));
    }

    [Fact]
    public async Task Empty_log_has_nothing_to_undo()
    {
        Assert.Null(await _log.LatestAsync(null, Ct));
    }

    [Fact]
    public void Fingerprint_depends_on_order_and_whole_ms_durations_only()
    {
        static LineupItem Item(string id, double ms) => new("content", id, ms, null, null);

        Assert.Equal(LineupFingerprint.Of([Item("a", 1000.2), Item("b", 2000)]), LineupFingerprint.Of([Item("a", 1000), Item("b", 2000)]));
        Assert.NotEqual(LineupFingerprint.Of([Item("a", 1000), Item("b", 2000)]), LineupFingerprint.Of([Item("b", 2000), Item("a", 1000)]));
        Assert.Equal(LineupFingerprint.Of([Item("a", 1000)]),
            LineupFingerprint.Of("""[{"type":"content","id":"a","durationMs":1000,"duration":1000}]"""));
    }

    private sealed class Factory(DbContextOptions<ChangeDbContext> options) : IDbContextFactory<ChangeDbContext>
    {
        public ChangeDbContext CreateDbContext() => new(options);
    }
}
