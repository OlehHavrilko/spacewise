using Spacewise.Catalog;
using Spacewise.Core;
using Spacewise.Data;

namespace Spacewise.Tests.Unit;

public sealed class RecycleBinTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "SpacewiseRb-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    public RecycleBinTests() => Directory.CreateDirectory(dir);

    // Only files the tests created in their own directory; no recursion.
    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
        Directory.Delete(dir);
    }

    [Fact]
    public void Preview_reports_shell_count_and_size_as_its_own_numbers()
    {
        RulePreview preview = SystemRules.RecycleBinPreview(12, 3456);
        Assert.Equal((12L, 3456L, 0), (preview.FileCount, preview.LogicalBytes, preview.Items.Count));
        Assert.Equal(SystemRules.RecycleBinAction, preview.Rule.Action);
        Assert.False(preview.Rule.RequiresAdmin);
        Assert.Equal(Consequence.Lost, preview.Rule.Consequence);
    }

    [Fact]
    public void Recycle_bin_never_enters_a_file_plan()
    {
        CleanupPlan plan = PlanBuilder.Build([SystemRules.RecycleBinPreview(5, 100)], Now);
        Assert.Empty(plan.Items);
        Assert.False(MutationPolicy.Evaluate(plan, Now).Allowed);
    }

    [Fact]
    public void Explanation_says_it_is_permanent_and_is_not_a_quarantine_promise()
    {
        string text = RuleExplainer.Explain(SystemRules.RecycleBinPreview(5, 100));
        Assert.Contains("Корзина очищается средствами Windows", text);
        Assert.Contains("необратимо", text);
        Assert.DoesNotContain("карантин, откуда", text);
    }

    private sealed class Keys : IDatabaseKeySource
    {
        public byte[] GetKey(string database) => Enumerable.Repeat((byte)3, 32).ToArray();
        public void Reset(string database) { }
    }

    [Fact]
    public async Task System_action_is_journaled_in_the_decision_chain()
    {
        await using var writer = new DatabaseWriter(SqliteDatabase.Open(Path.Combine(dir, "journal.db"), Schemas.Journal, new Keys()).Connection);
        var store = new ExecutionStore(writer);
        await store.RecordSystemActionAsync("windows.recyclebin", 3456, "Done", Now, TestContext.Current.CancellationToken);

        var rows = await store.RecentDecisionsAsync(5, TestContext.Current.CancellationToken);
        Assert.Equal(("shell:recycle-bin", "Done", 3456L), (rows.Single().Path, rows.Single().Result, rows.Single().Bytes));
        Assert.Null(await store.VerifyJournalAsync(TestContext.Current.CancellationToken));
    }
}
