using System.Security.Cryptography;
using Spacewise.Core;
using Spacewise.Data;

namespace Spacewise.Tests.Unit;

public sealed class QuarantineServiceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "SpacewiseQs-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private int databases;

    public QuarantineServiceTests() => Directory.CreateDirectory(dir);

    // Only files the tests created in their own directory; no recursion.
    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
        Directory.Delete(dir);
    }

    private sealed class Keys : IDatabaseKeySource
    {
        public byte[] GetKey(string database) => Enumerable.Repeat((byte)9, 32).ToArray();
        public void Reset(string database) { }
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private sealed class FakePurger(Func<string, RemoveStatus> behaviour, Action? onPurge = null) : IQuarantinePurger
    {
        public RemoveResult Purge(string storedPath) { onPurge?.Invoke(); return new(behaviour(storedPath)); }
    }

    private sealed class FakeRestorer : IFileRestorer
    {
        public RestoreResult Restore(string storedPath, string originalPath, byte[]? expectedSha256 = null) =>
            originalPath.EndsWith("bad") ? new(false, 0, true) : new(!originalPath.EndsWith("busy"));
    }

    private sealed class FakeSpace(Func<long?> free) : IVolumeSpace
    {
        public long? FreeBytes(string path) => free();
    }

    private async Task<(ExecutionStore Store, DatabaseWriter Writer)> Seed(params (string Original, string Stored, long Size)[] files)
    {
        var writer = new DatabaseWriter(SqliteDatabase.Open(Path.Combine(dir, $"journal{databases++}.db"), Schemas.Journal, new Keys()).Connection);
        PlanItem[] items = [.. files.Select(f => new PlanItem("r", 1, "delete", f.Original, f.Size, Now))];
        CleanupPlan plan = CleanupPlan.Create(items, Now, TimeSpan.FromHours(1), "user");
        long id = await new PlanStore(writer).SaveAsync(plan, cancellation: Ct);
        var store = new ExecutionStore(writer);
        byte[] sha = SHA256.HashData("x"u8);
        ItemOutcome[] outcomes = [.. plan.Items.Select(it => new ItemOutcome(it, RemoveStatus.Removed, 0, files.First(f => f.Original == it.Path).Stored, sha))];
        await store.RecordAsync(id, new ExecutionReport(outcomes, false), Now, TimeSpan.FromDays(30), Ct);
        return (store, writer);
    }

    [Fact]
    public async Task Sha256_and_expiry_are_stored_with_the_quarantine_row()
    {
        (ExecutionStore store, DatabaseWriter writer) = await Seed((@"C:\T\a", @"C:\Q\1", 5));
        await using (writer)
        {
            var held = await store.HeldAsync(Ct);
            Assert.Equal(SHA256.HashData("x"u8), held.Single().Sha256);
            Assert.Equal(Now.AddDays(30), held.Single().ExpiresUtc);
        }
    }

    [Fact]
    public async Task Expiry_purges_files_past_retention()
    {
        (ExecutionStore store, DatabaseWriter writer) = await Seed((@"C:\T\a", @"C:\Q\1", 5), (@"C:\T\b", @"C:\Q\2", 7));
        await using (writer)
        {
            var service = new QuarantineService(store, new FakePurger(_ => RemoveStatus.Removed), new FakeRestorer(), new FakeSpace(() => 1000), new Clock(Now.AddDays(31)));
            Assert.Equal(2, (await service.PurgeExpiredAsync(Ct)).Purged);
            Assert.Empty(await store.HeldAsync(Ct));
        }
    }

    [Fact]
    public async Task Expiry_keeps_files_still_within_retention()
    {
        (ExecutionStore store, DatabaseWriter writer) = await Seed((@"C:\T\c", @"C:\Q\3", 1));
        await using (writer)
        {
            var service = new QuarantineService(store, new FakePurger(_ => RemoveStatus.Removed), new FakeRestorer(), new FakeSpace(() => 1000), new Clock(Now.AddDays(5)));
            Assert.Equal(0, (await service.PurgeExpiredAsync(Ct)).Purged);
            Assert.Single(await store.HeldAsync(Ct));
        }
    }

    [Fact]
    public async Task Failed_purge_keeps_the_row_and_gone_file_counts_as_purged()
    {
        (ExecutionStore store, DatabaseWriter writer) = await Seed((@"C:\T\a", @"C:\Q\1", 5), (@"C:\T\b", @"C:\Q\2", 7), (@"C:\T\c", @"C:\Q\3", 9));
        await using (writer)
        {
            var purger = new FakePurger(p => p.EndsWith('1') ? RemoveStatus.Locked : p.EndsWith('2') ? RemoveStatus.Gone : RemoveStatus.Removed);
            var service = new QuarantineService(store, purger, new FakeRestorer(), new FakeSpace(() => 1000), new Clock(Now));
            PurgeReport report = await service.PurgeAllAsync(Ct);
            Assert.Equal((2, 1), (report.Purged, report.Failed));
            Assert.Equal(9, report.LogicalBytes);
            Assert.Equal([@"C:\Q\1"], (await store.HeldAsync(Ct)).Select(f => f.StoredPath));
        }
    }

    [Fact]
    public async Task Freed_space_is_measured_before_and_after_and_saved()
    {
        (ExecutionStore store, DatabaseWriter writer) = await Seed((@"C:\T\a", @"C:\Q\1", 5000));
        await using (writer)
        {
            long free = 1_000_000;
            var service = new QuarantineService(store, new FakePurger(_ => RemoveStatus.Removed, () => free += 5000), new FakeRestorer(), new FakeSpace(() => free), new Clock(Now));
            PurgeReport report = await service.PurgeAllAsync(Ct);
            Assert.Equal(5000, report.MeasuredFreeDelta);
            string json = await writer.ExecuteAsync(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT value_json FROM settings WHERE key = 'last_purge'";
                return (string)cmd.ExecuteScalar()!;
            }, Ct);
            Assert.Contains("\"free_before\":1000000", json);
            Assert.Contains("\"free_after\":1005000", json);
        }
    }

    [Fact]
    public async Task Unknown_volume_space_gives_no_measurement_but_still_purges()
    {
        (ExecutionStore store, DatabaseWriter writer) = await Seed((@"C:\T\a", @"C:\Q\1", 5));
        await using (writer)
        {
            var service = new QuarantineService(store, new FakePurger(_ => RemoveStatus.Removed), new FakeRestorer(), new FakeSpace(() => null), new Clock(Now));
            PurgeReport report = await service.PurgeAllAsync(Ct);
            Assert.Equal(1, report.Purged);
            Assert.Null(report.MeasuredFreeDelta);
        }
    }

    [Fact]
    public async Task Restore_marks_restored_and_leaves_failures_held()
    {
        (ExecutionStore store, DatabaseWriter writer) = await Seed((@"C:\T\a", @"C:\Q\1", 5), (@"C:\T\busy", @"C:\Q\2", 7));
        await using (writer)
        {
            var service = new QuarantineService(store, new FakePurger(_ => RemoveStatus.Removed), new FakeRestorer(), new FakeSpace(() => 1), new Clock(Now));
            RestoreReport report = await service.RestoreAllAsync(Ct);
            Assert.Equal((1, 1), (report.Restored, report.Failed));
            Assert.Equal([@"C:\T\busy"], (await store.HeldAsync(Ct)).Select(f => f.OriginalPath));
        }
    }

    [Fact]
    public async Task Restore_reports_hash_mismatch_separately_and_keeps_the_file_held()
    {
        (ExecutionStore store, DatabaseWriter writer) = await Seed((@"C:\T\a", @"C:\Q\1", 5), (@"C:\T\bad", @"C:\Q\2", 7));
        await using (writer)
        {
            var service = new QuarantineService(store, new FakePurger(_ => RemoveStatus.Removed), new FakeRestorer(), new FakeSpace(() => 1), new Clock(Now));
            RestoreReport report = await service.RestoreAllAsync(Ct);
            Assert.Equal((1, 0, 1), (report.Restored, report.Failed, report.HashMismatch));
            Assert.Equal([@"C:\T\bad"], (await store.HeldAsync(Ct)).Select(f => f.OriginalPath));
        }
    }
}
