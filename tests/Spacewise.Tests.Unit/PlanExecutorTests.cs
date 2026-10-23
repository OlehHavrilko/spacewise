using Spacewise.Core;

namespace Spacewise.Tests.Unit;

public sealed class PlanExecutorTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly UserFolders Folders = new(@"C:\Users\u", @"C:\Users\u\AppData\Local", @"C:\Users\u\AppData\Roaming",
        @"C:\Users\u\AppData\Local\Temp", @"C:\Windows", @"C:\ProgramData", @"C:\Program Files", @"C:\Program Files (x86)",
        @"C:\Users\u\AppData\Local\Spacewise", []);

    private const string Allowed = @"C:\Users\u\AppData\Local\Temp\a.tmp";

    private sealed class FakeRemover(Func<PlanItem, RemoveResult> behaviour) : IFileRemover
    {
        public List<string> Calls { get; } = [];
        public RemoveResult Remove(PlanItem item) { Calls.Add(item.Path); return behaviour(item); }
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlanItem Item(string path, long size = 10, string action = "delete") => new("temp.user", 1, action, path, size, Now.AddDays(-9));

    private static CleanupPlan Plan(params PlanItem[] items) => CleanupPlan.Create(items, Now, TimeSpan.FromHours(1), "user");

    private static PlanExecutor Executor(FakeRemover remover, DateTime? now = null) => new(remover, Folders, new Clock(now ?? Now.AddMinutes(5)));

    [Fact]
    public void Runs_every_item_and_counts_removed_bytes()
    {
        var remover = new FakeRemover(_ => new(RemoveStatus.Removed));
        ExecutionReport report = Executor(remover).Execute(Plan(Item(Allowed, 10), Item(@"C:\Users\u\AppData\Local\Temp\b.tmp", 5)), Ct);
        Assert.Equal(2, report.Removed);
        Assert.Equal(15, report.BytesRemoved);
        Assert.Equal(2, remover.Calls.Count);
    }

    [Fact]
    public void Skipped_item_does_not_stop_the_rest_and_is_not_counted()
    {
        var remover = new FakeRemover(i => new(i.Path == Allowed ? RemoveStatus.Locked : RemoveStatus.Removed));
        ExecutionReport report = Executor(remover).Execute(Plan(Item(Allowed), Item(@"C:\Users\u\AppData\Local\Temp\b.tmp")), Ct);
        Assert.Equal(1, report.Removed);
        Assert.Equal(RemoveStatus.Locked, report.Outcomes.Single(o => o.Item.Path == Allowed).Status);
    }

    [Fact]
    public void Protected_path_or_unknown_action_never_reaches_the_remover()
    {
        var remover = new FakeRemover(_ => new(RemoveStatus.Removed));
        ExecutionReport report = Executor(remover).Execute(Plan(Item(@"C:\Windows\System32\kernel32.dll"), Item(Allowed, action: "format")), Ct);
        Assert.Empty(remover.Calls);
        Assert.All(report.Outcomes, o => Assert.Equal(RemoveStatus.Unsafe, o.Status));
    }

    [Fact]
    public void Tampered_plan_is_rejected_before_any_call()
    {
        var remover = new FakeRemover(_ => new(RemoveStatus.Removed));
        CleanupPlan tampered = Plan(Item(Allowed)) with { Items = [Item(Allowed, 999)] };
        Assert.Throws<PlanRejectedException>(() => Executor(remover).Execute(tampered, Ct));
        Assert.Empty(remover.Calls);
    }

    [Fact]
    public void Expired_plan_is_rejected()
    {
        var remover = new FakeRemover(_ => new(RemoveStatus.Removed));
        Assert.Throws<PlanRejectedException>(() => Executor(remover, Now.AddHours(2)).Execute(Plan(Item(Allowed)), Ct));
        Assert.Empty(remover.Calls);
    }

    [Fact]
    public void Cancellation_stops_between_items()
    {
        using var cts = new CancellationTokenSource();
        var remover = new FakeRemover(_ => { cts.Cancel(); return new(RemoveStatus.Removed); });
        ExecutionReport report = Executor(remover).Execute(Plan(Item(Allowed), Item(@"C:\Users\u\AppData\Local\Temp\b.tmp")), cts.Token);
        Assert.True(report.Cancelled);
        Assert.Single(remover.Calls);
    }

    [Fact]
    public void IO_exception_becomes_failed_outcome()
    {
        var remover = new FakeRemover(_ => throw new IOException("boom"));
        ExecutionReport report = Executor(remover).Execute(Plan(Item(Allowed)), Ct);
        Assert.Equal(RemoveStatus.Failed, report.Outcomes.Single().Status);
    }
}
