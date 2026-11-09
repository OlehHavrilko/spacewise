using Spacewise.Core;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

public sealed class WindowsCleanupPolicyTests
{
    [Fact]
    public void Exclusion_changed_after_preview_is_rechecked_before_permanent_removal()
    {
        using var fx = new FsFixture();
        UserFolders folders = UserFolders.FromEnvironment();
        string file = fx.File("keep.bin", 8);
        if (!ProtectedPaths.Check(file, folders).Allowed) return;
        IReadOnlyList<string> exclusions = [];
        var policy = new WindowsCleanupPolicy(folders, () => exclusions, Path.Combine(fx.Root, "app"));
        var remover = new HandleFileRemover(requireIdentity: true);
        var item = new PlanItem("test.cache", 1, "delete", file, 8, File.GetLastWriteTimeUtc(file), FileMetadata.TryRead(file)!.Identity);
        var plan = CleanupPlan.Create([item], DateTime.UtcNow, TimeSpan.FromMinutes(1), "test");
        Assert.True(policy.Check(file).Allowed);
        exclusions = [file];
        var report = new PlanExecutor(remover, policy, TimeProvider.System).Execute(plan, TestContext.Current.CancellationToken);
        Assert.Equal(RemoveStatus.Unsafe, Assert.Single(report.Outcomes).Status);
        Assert.True(File.Exists(file));
    }
}
