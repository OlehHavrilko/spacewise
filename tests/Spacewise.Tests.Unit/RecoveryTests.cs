using Spacewise.Core;
using Spacewise.Data;

namespace Spacewise.Tests.Unit;

public sealed class RecoveryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "SpacewiseRec-" + Guid.NewGuid().ToString("N"));
    private readonly string quarantine;
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mtime = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RecoveryTests()
    {
        Directory.CreateDirectory(dir);
        quarantine = Path.Combine(dir, "q");
        Directory.CreateDirectory(quarantine);
    }

    // Only files the tests created in their own directories; no recursion.
    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(quarantine)) File.Delete(file);
        Directory.Delete(quarantine);
        foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
        Directory.Delete(dir);
    }

    private sealed class Keys : IDatabaseKeySource
    {
        public byte[] GetKey(string database) => Enumerable.Repeat((byte)5, 32).ToArray();
        public void Reset(string database) { }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private string MakeFile(string folder, string name, int bytes)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Mtime);
        return path;
    }

    private async Task<(DatabaseWriter Writer, ExecutionStore Store, PlanStore Plans, long PlanId, CleanupPlan Plan)> Begin(params string[] paths)
    {
        var writer = new DatabaseWriter(SqliteDatabase.Open(Path.Combine(dir, "journal.db"), Schemas.Journal, new Keys()).Connection);
        PlanItem[] items = [.. paths.Select(p => new PlanItem("r", 1, "delete", p, new FileInfo(p).Length, Mtime))];
        CleanupPlan plan = CleanupPlan.Create(items, Now, TimeSpan.FromHours(1), "user");
        var plans = new PlanStore(writer);
        long id = await plans.SaveAsync(plan, cancellation: Ct);
        return (writer, new ExecutionStore(writer), plans, id, plan);
    }

    private static async Task<string[]> States(DatabaseWriter writer) =>
        await writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT state || ':' || coalesce(skip_reason, '') FROM plan_items ORDER BY seq";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list.ToArray();
        }, Ct);

    private static async Task<string> PlanState(DatabaseWriter writer) =>
        await writer.ExecuteAsync(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT state FROM plans"; return (string)cmd.ExecuteScalar()!; }, Ct);

    private ExecutionRecovery Recovery(ExecutionStore store, PlanStore plans) => new(store, plans, quarantine, new Clock());

    [Fact]
    public async Task Item_that_was_never_moved_is_settled_as_interrupted_and_stays_in_place()
    {
        string file = MakeFile(dir, "a.bin", 10);
        (DatabaseWriter writer, ExecutionStore store, PlanStore plans, long id, _) = await Begin(file);
        await using (writer)
        {
            await store.BeginItemAsync(id, 0, Ct);
            Assert.Equal(["in_progress:"], await States(writer));

            RecoveryReport report = await Recovery(store, plans).RecoverAsync(Ct);

            Assert.Equal((1, 0, 0), (report.NeverMoved, report.Recovered, report.Unknown));
            Assert.Equal(["skipped:Interrupted"], await States(writer));
            Assert.Equal("interrupted", await PlanState(writer));
            Assert.True(File.Exists(file));
        }
    }

    [Fact]
    public async Task Item_moved_before_the_crash_is_found_in_quarantine_and_becomes_restorable()
    {
        string file = MakeFile(dir, "b.bin", 20);
        (DatabaseWriter writer, ExecutionStore store, PlanStore plans, long id, _) = await Begin(file);
        await using (writer)
        {
            await store.BeginItemAsync(id, 0, Ct);
            string stored = MakeFile(quarantine, "0123456789abcdef0123456789abcdef", 20);
            File.Delete(file);

            RecoveryReport report = await Recovery(store, plans).RecoverAsync(Ct);

            Assert.Equal((0, 1, 0, 0), (report.NeverMoved, report.Recovered, report.Unknown, report.UnrecordedFiles));
            Assert.Equal(["done:"], await States(writer));
            var held = await store.HeldAsync(Ct);
            Assert.Equal((file, stored), (held.Single().OriginalPath, held.Single().StoredPath));
            Assert.Null(await store.VerifyJournalAsync(Ct));
        }
    }

    [Fact]
    public async Task Item_that_is_neither_in_place_nor_in_quarantine_is_reported_unknown()
    {
        string file = MakeFile(dir, "c.bin", 30);
        (DatabaseWriter writer, ExecutionStore store, PlanStore plans, long id, _) = await Begin(file);
        await using (writer)
        {
            await store.BeginItemAsync(id, 0, Ct);
            File.Delete(file);
            RecoveryReport report = await Recovery(store, plans).RecoverAsync(Ct);
            Assert.Equal((0, 0, 1), (report.NeverMoved, report.Recovered, report.Unknown));
            Assert.Equal(["skipped:Interrupted"], await States(writer));
        }
    }

    [Fact]
    public async Task Quarantine_files_without_a_row_are_counted_not_guessed()
    {
        string file = MakeFile(dir, "d.bin", 5);
        (DatabaseWriter writer, ExecutionStore store, PlanStore plans, _, _) = await Begin(file);
        await using (writer)
        {
            MakeFile(quarantine, "ffffffffffffffffffffffffffffffff", 999);
            RecoveryReport report = await Recovery(store, plans).RecoverAsync(Ct);
            Assert.Equal(1, report.UnrecordedFiles);
            Assert.Empty(await store.HeldAsync(Ct));
        }
    }

    [Fact]
    public async Task Executor_journal_records_each_item_before_the_next_so_a_crash_leaves_one_in_doubt()
    {
        string a = MakeFile(dir, "e1.bin", 1), b = MakeFile(dir, "e2.bin", 2), c = MakeFile(dir, "e3.bin", 3);
        (DatabaseWriter writer, ExecutionStore store, _, long id, CleanupPlan plan) = await Begin(a, b, c);
        await using (writer)
        {
            // Temp is inside the allowed roots; the remover "crashes" on the second item.
            UserFolders folders = UserFolders.FromEnvironment();
            if (!plan.Items.All(i => ProtectedPaths.Check(i.Path, folders).Allowed)) return;
            int calls = 0;
            var remover = new CrashingRemover(() => ++calls == 2);
            var executor = new PlanExecutor(remover, folders, new Clock());

            await Task.Run(() => Assert.Throws<InvalidOperationException>(() => executor.Execute(plan, Ct, store.JournalFor(id, new Clock()))), Ct);

            string[] states = await States(writer);
            Assert.Equal(3, states.Length);
            Assert.Equal(["done:", "in_progress:", "pending:"], states);
        }
    }

    private sealed class CrashingRemover(Func<bool> crash) : IFileRemover
    {
        public RemoveResult Remove(PlanItem item) => crash() ? throw new InvalidOperationException("simulated crash") : new(RemoveStatus.Removed);
    }
}
