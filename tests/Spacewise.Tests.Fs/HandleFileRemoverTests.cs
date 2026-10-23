using Spacewise.Core;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

public sealed class HandleFileRemoverTests : IDisposable
{
    private readonly FsFixture fx = new();
    private readonly HandleFileRemover remover = new();

    public void Dispose() => fx.Dispose();

    private static PlanItem ItemFor(string path) =>
        new("test", 1, "delete", path, new FileInfo(path).Length, new FileInfo(path).LastWriteTimeUtc);

    [Fact]
    public void Removes_file_matching_the_plan()
    {
        string file = fx.File("a.bin", 10);
        Assert.Equal(RemoveStatus.Removed, remover.Remove(ItemFor(file)).Status);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Removes_read_only_file()
    {
        string file = fx.File("ro.bin", 10);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        Assert.Equal(RemoveStatus.Removed, remover.Remove(ItemFor(file)).Status);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Missing_file_is_gone_not_failed()
    {
        string file = fx.File("b.bin", 10);
        PlanItem item = ItemFor(file);
        File.Delete(file);
        Assert.Equal(RemoveStatus.Gone, remover.Remove(item).Status);
        Assert.Equal(RemoveStatus.Gone, remover.Remove(item with { Path = Path.Combine(fx.Root, "no-dir", "x") }).Status);
    }

    [Fact]
    public void Changed_size_or_mtime_is_skipped_and_file_kept()
    {
        string file = fx.File("c.bin", 10);
        PlanItem item = ItemFor(file);
        Assert.Equal(RemoveStatus.Changed, remover.Remove(item with { ExpectedSize = 11 }).Status);
        Assert.Equal(RemoveStatus.Changed, remover.Remove(item with { ExpectedMtimeUtc = item.ExpectedMtimeUtc.AddSeconds(1) }).Status);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Junction_in_the_parent_chain_is_refused_and_target_kept()
    {
        fx.Dir("real");
        string victim = fx.File(@"real\victim.bin", 5);
        fx.Junction("link", Path.Combine(fx.Root, "real"));
        PlanItem viaLink = ItemFor(victim) with { Path = Path.Combine(fx.Root, "link", "victim.bin") };

        Assert.Equal(RemoveStatus.Unsafe, remover.Remove(viaLink).Status);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void Deleting_one_hard_link_keeps_the_other_name()
    {
        string first = fx.File("h1.bin", 10);
        string second = fx.HardLink("h2.bin", first);
        Assert.Equal(RemoveStatus.Removed, remover.Remove(ItemFor(first)).Status);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void File_open_without_delete_sharing_is_locked_and_kept()
    {
        string file = fx.File("l.bin", 10);
        PlanItem item = ItemFor(file);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(RemoveStatus.Locked, remover.Remove(item).Status);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Directory_is_never_removed()
    {
        string dir = fx.Dir("d");
        PlanItem item = new("test", 1, "delete", dir, 0, new DirectoryInfo(dir).LastWriteTimeUtc);
        Assert.NotEqual(RemoveStatus.Removed, remover.Remove(item).Status);
        Assert.True(Directory.Exists(dir));
    }
}
