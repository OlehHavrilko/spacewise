using System.Text;
using Microsoft.Data.Sqlite;
using Spacewise.Core;
using Spacewise.Data;

namespace Spacewise.Tests.Unit;

public sealed class StorageTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "SpacewiseData-" + Guid.NewGuid().ToString("N"));
    private readonly FixedKeys keys = new();

    public StorageTests() => Directory.CreateDirectory(dir);

    // Only files the tests created in their own directory; no recursion.
    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
        Directory.Delete(dir);
    }

    private string Db(string name = "cache.db") => Path.Combine(dir, name);

    [Fact]
    public void New_database_gets_current_schema_and_pragmas()
    {
        using SqliteConnection c = SqliteDatabase.Open(Db(), Schemas.Cache, keys).Connection;
        Assert.Equal((long)Schemas.Cache.Version, Scalar(c, "PRAGMA user_version"));
        Assert.Equal("wal", Scalar(c, "PRAGMA journal_mode"));
        Assert.Equal(1L, Scalar(c, "PRAGMA foreign_keys"));
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM sqlite_master WHERE name = 'top_files'"));
    }

    [Fact]
    public void Journal_uses_full_synchronous()
    {
        using SqliteConnection c = SqliteDatabase.Open(Db("journal.db"), Schemas.Journal, keys).Connection;
        Assert.Equal(2L, Scalar(c, "PRAGMA synchronous"));
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM sqlite_master WHERE name = 'decision_journal'"));
    }

    [Fact]
    public void File_is_encrypted_at_rest()
    {
        using (SqliteConnection c = SqliteDatabase.Open(Db(), Schemas.Cache, keys).Connection)
            Exec(c, "INSERT INTO paths(parent_id, name) VALUES(0, 'secret-folder-name')");
        byte[] raw = File.ReadAllBytes(Db());
        Assert.False(Encoding.ASCII.GetString(raw, 0, 15) == "SQLite format 3");
        Assert.DoesNotContain("secret-folder-name", Encoding.UTF8.GetString(raw) + ReadWal());
    }

    [Fact]
    public void Wrong_key_sets_file_aside_and_starts_fresh()
    {
        using (SqliteConnection c = SqliteDatabase.Open(Db(), Schemas.Cache, keys).Connection)
            Exec(c, "INSERT INTO paths(parent_id, name) VALUES(0, 'C:')");
        keys.Rotate();

        OpenedDatabase opened = SqliteDatabase.Open(Db(), Schemas.Cache, keys);
        using SqliteConnection fresh = opened.Connection;

        Assert.NotNull(opened.RecoveredFrom);
        Assert.True(File.Exists(opened.RecoveredFrom));
        Assert.Equal(1, keys.Resets);
        Assert.Equal(0L, Scalar(fresh, "SELECT count(*) FROM paths"));
    }

    [Fact]
    public void Upgrade_backs_up_and_applies_new_migrations()
    {
        var v1 = new DatabaseSchema("test", false, ["CREATE TABLE a(x INTEGER);"]);
        var v2 = v1 with { Migrations = [.. v1.Migrations, "ALTER TABLE a ADD COLUMN y TEXT;"] };
        using (SqliteConnection c = SqliteDatabase.Open(Db("t.db"), v1, keys).Connection)
            Exec(c, "INSERT INTO a(x) VALUES(7)");

        using (SqliteConnection c = SqliteDatabase.Open(Db("t.db"), v2, keys).Connection)
        {
            Assert.Equal(2L, Scalar(c, "PRAGMA user_version"));
            Assert.Equal(7L, Scalar(c, "SELECT x FROM a WHERE y IS NULL"));
        }

        // The backup is encrypted with the same key and still at version 1.
        OpenedDatabase backup = SqliteDatabase.Open(Db("t.db.v1.bak"), v1, keys);
        using SqliteConnection b = backup.Connection;
        Assert.Null(backup.RecoveredFrom);
        Assert.Equal(1L, Scalar(b, "PRAGMA user_version"));
        Assert.Equal(7L, Scalar(b, "SELECT x FROM a"));
    }

    [Fact]
    public void Newer_schema_is_refused()
    {
        var v2 = new DatabaseSchema("test", false, ["CREATE TABLE a(x);", "CREATE TABLE b(x);"]);
        SqliteDatabase.Open(Db("t.db"), v2, keys).Connection.Dispose();
        var v1 = v2 with { Migrations = v2.Migrations.Take(1).ToList() };
        Assert.Throws<InvalidOperationException>(() => SqliteDatabase.Open(Db("t.db"), v1, keys));
    }

    [Fact]
    public async Task Writer_runs_work_in_order_and_rejects_after_dispose()
    {
        var writer = new DatabaseWriter(SqliteDatabase.Open(Db(), Schemas.Cache, keys).Connection);
        var order = new List<int>();
        Task[] work = [.. Enumerable.Range(0, 50).Select(i => writer.ExecuteAsync(_ => { order.Add(i); return i; }, Ct))];
        await Task.WhenAll(work);
        Assert.Equal(Enumerable.Range(0, 50), order);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ExecuteAsync<int>(_ => throw new InvalidOperationException(), Ct));
        await writer.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => { _ = writer.ExecuteAsync(_ => 0, Ct); });
    }

    [Fact]
    public async Task Scan_round_trips_through_cache()
    {
        var identity = new FileIdentity(0xDEADBEEF, UInt128.MaxValue - 5);
        var result = new ScanResult(Guid.NewGuid(), @"C:\Data", ScanState.Partial, 10, 3000, 2, 1,
            [
                new FileFinding(@"C:\Data\big.bin", 2000, 2048, identity, 1),
                new FileFinding(@"C:\Data\sub\link.bin", 900, 1024, identity with { FileId = 7 }, 2),
                new FileFinding(@"C:\Data\sub\plain.txt", 100),
            ], [])
        { SkippedByPolicy = 1, UnvisitedDirectories = 3, StoppedEarly = true };
        var completed = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        await using var writer = new DatabaseWriter(SqliteDatabase.Open(Db(), Schemas.Cache, keys).Connection);
        var store = new ScanStore(writer);
        long id = await store.SaveAsync(result, completed, Ct);
        await store.SaveAsync(result with { ScanId = Guid.NewGuid(), Root = @"\\server\share", LargestFiles = [] }, completed, Ct);

        IReadOnlyList<StoredScan> recent = await store.RecentAsync(5, Ct);
        Assert.Equal([@"\\server\share", @"C:\Data"], recent.Select(s => s.Root));
        StoredScan scan = recent[1];
        Assert.Equal((id, result.ScanId, ScanState.Partial, completed, 10L, 3000L), (scan.Id, scan.ScanId, scan.State, scan.CompletedUtc, scan.Files, scan.LogicalBytes));
        Assert.Equal((1L, 1L, 3L, true), (scan.ExcludedByPolicy, scan.Inaccessible, scan.UnvisitedDirectories, scan.StoppedEarly));

        Assert.Equal(result.LargestFiles, await store.TopFilesAsync(id, Ct));
        // "C:", "Data", "big.bin", "sub", "link.bin", "plain.txt", "\\server", "share": shared prefixes stored once.
        Assert.Equal(8L, await writer.ExecuteAsync(c => Scalar(c, "SELECT count(*) FROM paths"), Ct));
    }

    [Fact]
    public async Task Trim_keeps_the_newest_scans_and_removes_unreferenced_path_nodes()
    {
        await using var writer = new DatabaseWriter(SqliteDatabase.Open(Db(), Schemas.Cache, keys).Connection);
        var store = new ScanStore(writer);
        var done = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 8; i++)
            await store.SaveAsync(new ScanResult(Guid.NewGuid(), $@"C:\Root{i}", ScanState.Completed, 1, 10, 0, 0,
                [new FileFinding($@"C:\Root{i}\deep\er\file{i}.bin", 10)], []), done.AddMinutes(i), Ct);
        long Count(string table) => writer.ExecuteAsync(c => { using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT count(*) FROM {table}"; return (long)cmd.ExecuteScalar()!; }, Ct).Result;
        long pathsBefore = Count("paths");

        Assert.Equal(3, await store.TrimAsync(cancellation: Ct));

        IReadOnlyList<StoredScan> left = await store.RecentAsync(10, Ct);
        Assert.Equal(["C:\\Root7", "C:\\Root6", "C:\\Root5", "C:\\Root4", "C:\\Root3"], left.Select(s => s.Root));
        Assert.Equal(5, Count("top_files"));
        Assert.True(Count("paths") < pathsBefore);
        Assert.Equal(@"C:\Root3\deep\er\file3.bin", (await store.TopFilesAsync(left[^1].Id, Ct)).Single().Path);
        Assert.Equal(0, await store.TrimAsync(cancellation: Ct));
    }

    [Theory]
    [InlineData(ScanState.Idle)]
    [InlineData(ScanState.Scanning)]
    public async Task Unfinished_scan_is_not_saved(ScanState state)
    {
        await using var writer = new DatabaseWriter(SqliteDatabase.Open(Db(), Schemas.Cache, keys).Connection);
        var result = new ScanResult(Guid.NewGuid(), @"C:\", state, 0, 0, 0, 0, [], []);
        await Assert.ThrowsAsync<ArgumentException>(() => new ScanStore(writer).SaveAsync(result, DateTime.UtcNow, Ct));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ReadWal() => File.Exists(Db() + "-wal") ? Encoding.UTF8.GetString(File.ReadAllBytes(Db() + "-wal")) : "";

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using SqliteCommand command = c.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using SqliteCommand command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class FixedKeys : IDatabaseKeySource
    {
        private byte seed = 1;
        public int Resets { get; private set; }
        public byte[] GetKey(string database) => Enumerable.Repeat(seed, 32).ToArray();
        public void Reset(string database) { Resets++; }
        public void Rotate() => seed++;
    }
}
