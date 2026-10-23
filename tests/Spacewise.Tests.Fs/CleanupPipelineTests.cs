using Spacewise.Core;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

// Plan -> executor -> quarantine -> restore on a real NTFS tree, with the real protected-path policy.
public sealed class CleanupPipelineTests : IDisposable
{
    private readonly FsFixture fx = new();
    private readonly string quarantine;
    private readonly QuarantineFileRemover remover;

    public CleanupPipelineTests()
    {
        quarantine = fx.Dir("quarantine");
        remover = new QuarantineFileRemover(quarantine);
    }

    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(quarantine)) File.Delete(file);
        fx.Dispose();
    }

    private static PlanItem ItemFor(string path) =>
        new("test", 1, "delete", path, new FileInfo(path).Length, new FileInfo(path).LastWriteTimeUtc);

    [Fact]
    public void Executed_plan_quarantines_files_and_restore_brings_them_back()
    {
        UserFolders folders = UserFolders.FromEnvironment();
        string root = fx.Root;
        if (!ProtectedPaths.Check(Path.Combine(root, "x"), folders).Allowed) return; // fixture volume outside the allowed roots
        string a = fx.File("a.bin", 10), b = fx.File("b.bin", 20);
        var now = DateTime.UtcNow;
        CleanupPlan plan = CleanupPlan.Create([ItemFor(a), ItemFor(b), new("test", 1, "delete", @"C:\Windows\System32\kernel32.dll", 1, now)], now, TimeSpan.FromHours(1), "user");

        ExecutionReport report = new PlanExecutor(remover, folders, TimeProvider.System).Execute(plan, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Removed);
        Assert.Equal(30, report.BytesRemoved);
        Assert.Contains(report.Outcomes, o => o.Item.Path.EndsWith("kernel32.dll") && o.Status == RemoveStatus.Unsafe);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        foreach (ItemOutcome o in report.Outcomes.Where(o => o.Status == RemoveStatus.Removed))
            Assert.True(remover.Restore(o.StoredPath!, o.Item.Path).Restored);
        Assert.Equal(10, new FileInfo(a).Length);
        Assert.Equal(20, new FileInfo(b).Length);
    }
}
