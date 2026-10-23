using Spacewise.Core;
using Spacewise.Data;

namespace Spacewise.Tests.Unit;

public sealed class PlanTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "SpacewisePlan-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    public PlanTests() => Directory.CreateDirectory(dir);

    // Only files the tests created in their own directory; no recursion.
    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
        Directory.Delete(dir);
    }

    private static PlanItem Item(string path, long size = 10, string rule = "temp.user") =>
        new(rule, 1, "delete", path, size, Now.AddDays(-30));

    private static CleanupPlan Plan(params PlanItem[] items) => CleanupPlan.Create(items, Now, TimeSpan.FromHours(1), "user");

    [Fact]
    public void Digest_ignores_input_order_and_is_stable()
    {
        PlanItem a = Item(@"C:\Users\u\AppData\Local\Temp\a"), b = Item(@"C:\Users\u\AppData\Local\Temp\b");
        Assert.Equal(Plan(a, b).Digest, Plan(b, a).Digest);
        Assert.Equal(32, Plan(a).Digest.Length);
    }

    [Fact]
    public void Digest_changes_with_any_field()
    {
        PlanItem a = Item(@"C:\Temp\a");
        byte[] baseline = Plan(a).Digest;
        Assert.NotEqual(baseline, Plan(a with { Path = @"C:\Temp\b" }).Digest);
        Assert.NotEqual(baseline, Plan(a with { ExpectedSize = 11 }).Digest);
        Assert.NotEqual(baseline, Plan(a with { ExpectedMtimeUtc = Now }).Digest);
        Assert.NotEqual(baseline, Plan(a with { RuleVersion = 2 }).Digest);
        Assert.NotEqual(baseline, Plan(a, Item(@"C:\Temp\b")).Digest);
    }

    [Fact]
    public void Digest_fields_cannot_be_shifted_across_boundaries()
    {
        Assert.NotEqual(Plan(Item("ab", rule: "c")).Digest, Plan(Item("b", rule: "ac")).Digest);
    }

    [Fact]
    public void Tampered_items_fail_DigestMatches()
    {
        CleanupPlan plan = Plan(Item(@"C:\Temp\a"));
        Assert.True(plan.DigestMatches());
        Assert.False((plan with { Items = [Item(@"C:\Windows\System32\x")] }).DigestMatches());
    }

    [Fact]
    public async Task Saved_plan_loads_back_with_same_digest_and_draft_state()
    {
        var keys = new TestKeys();
        await using var writer = new DatabaseWriter(SqliteDatabase.Open(Path.Combine(dir, "journal.db"), Schemas.Journal, keys).Connection);
        var store = new PlanStore(writer);
        CleanupPlan plan = Plan(Item(@"C:\Temp\a", 5), Item(@"C:\Temp\я файл.txt", 7));

        long id = await store.SaveAsync(plan, cancellation: TestContext.Current.CancellationToken);
        CleanupPlan? loaded = await store.LoadAsync(id, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(plan.Digest, loaded.Digest);
        Assert.Equal(plan.Items, loaded.Items);
        Assert.Equal(plan.ExpiresUtc, loaded.ExpiresUtc);
        Assert.Equal(12, loaded.ExpectedBytes);
        Assert.Null(await store.LoadAsync(id + 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_rejects_row_whose_items_were_edited()
    {
        var keys = new TestKeys();
        var db = SqliteDatabase.Open(Path.Combine(dir, "journal.db"), Schemas.Journal, keys);
        await using var writer = new DatabaseWriter(db.Connection);
        var store = new PlanStore(writer);
        long id = await store.SaveAsync(Plan(Item(@"C:\Temp\a")), cancellation: TestContext.Current.CancellationToken);
        await writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE plan_items SET expected_size = 999";
            return cmd.ExecuteNonQuery();
        }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Execution_report_updates_item_states_actions_and_plan_state()
    {
        var keys = new TestKeys();
        await using var writer = new DatabaseWriter(SqliteDatabase.Open(Path.Combine(dir, "journal.db"), Schemas.Journal, keys).Connection);
        var store = new PlanStore(writer);
        CleanupPlan plan = Plan(Item(@"C:\Temp", 5), Item(@"C:\Temp", 7), Item(@"C:\Temp\c", 9));
        long id = await store.SaveAsync(plan, cancellation: TestContext.Current.CancellationToken);
        var report = new ExecutionReport(
            [new(plan.Items[0], RemoveStatus.Removed, 0, @"C:\Q\abc"), new(plan.Items[1], RemoveStatus.Locked, 32)], Cancelled: true);

        await new ExecutionStore(writer).RecordAsync(id, report, Now, cancellation: TestContext.Current.CancellationToken);

        string[] rows = await writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT state || ':' || coalesce(skip_reason, '') FROM plan_items ORDER BY seq";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            r.Close();
            cmd.CommandText = "SELECT state FROM plans";
            list.Add((string)cmd.ExecuteScalar()!);
            cmd.CommandText = "SELECT count(*) || ':' || sum(native_reported) FROM actions";
            list.Add((string)cmd.ExecuteScalar()!);
            cmd.CommandText = "SELECT count(*) || ':' || size || ':' || expires_utc FROM quarantine";
            list.Add((string)cmd.ExecuteScalar()!);
            return list.ToArray();
        }, TestContext.Current.CancellationToken);
        Assert.Equal(["done:", "skipped:Locked", "pending:", "cancelled", "2:5", "1:5:2026-11-22T12:00:00.0000000Z"], rows);
    }

    private sealed class TestKeys : IDatabaseKeySource
    {
        public byte[] GetKey(string database) => Enumerable.Repeat((byte)7, 32).ToArray();
        public void Reset(string database) { }
    }
}
