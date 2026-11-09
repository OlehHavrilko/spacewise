using Spacewise.Core;
using Spacewise.Data;

namespace Spacewise.Tests.Unit;

public sealed class FolderHistoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SpacewiseTrend-" + Guid.NewGuid().ToString("N"));
    private sealed class Keys : IDatabaseKeySource
    {
        public byte[] GetKey(string database) => new byte[32];
        public void Reset(string database) => throw new InvalidOperationException();
    }
    private DatabaseWriter Writer() => new(SqliteDatabase.Open(Path.Combine(directory, "cache.db"), Schemas.Cache, new Keys()).Connection);
    private ScanResult Scan(long size, ScanState state = ScanState.Completed) => new(Guid.NewGuid(), directory, state, 1, size, 0, 0, [], [])
    { Folders = [new(Path.Combine(directory, "child"), 1, size)] };

    [Fact]
    public async Task Daily_snapshot_is_replaced_and_shrink_is_preserved()
    {
        await using var writer = Writer(); var history = new FolderHistoryStore(writer);
        DateTime day = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
        await history.SaveAsync(Scan(100), day, TestContext.Current.CancellationToken);
        await history.SaveAsync(Scan(20), day.AddHours(2), TestContext.Current.CancellationToken);
        var point = Assert.Single(await history.PointsAsync(directory, TestContext.Current.CancellationToken));
        Assert.Equal(20, point.LogicalBytes);
        Assert.Equal(2, (await history.PathsAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task Old_history_is_trimmed_and_partial_scans_are_not_trusted()
    {
        await using var writer = Writer(); var history = new FolderHistoryStore(writer);
        DateTime day = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
        await history.SaveAsync(Scan(100), day.AddDays(-32), TestContext.Current.CancellationToken);
        await history.SaveAsync(Scan(20), day, TestContext.Current.CancellationToken);
        await history.SaveAsync(Scan(999, ScanState.Partial), day.AddDays(1), TestContext.Current.CancellationToken);
        Assert.Equal(20, Assert.Single(await history.PointsAsync(directory, TestContext.Current.CancellationToken)).LogicalBytes);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
