using Spacewise.Core;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

public sealed class FileSystemTreeTests : IDisposable
{
    private readonly FsFixture fx = new();

    public void Dispose() => fx.Dispose();

    [Fact]
    public void Files_recurse_but_never_enter_a_junction()
    {
        fx.Dir("outside");
        fx.File(@"outside\secret.bin", 10);
        string root = fx.Dir("root");
        fx.Dir(@"root\sub");
        fx.File(@"root\a.txt", 3);
        fx.File(@"root\sub\b.txt", 4);
        fx.Junction(@"root\link", Path.Combine(fx.Root, "outside"));

        FileEntry[] files = [.. new FileSystemTree().Files(root, TestContext.Current.CancellationToken)];

        Assert.Equal([@"root\a.txt", @"root\sub\b.txt"], files.Select(f => Path.GetRelativePath(fx.Root, f.Path)).Order());
        Assert.Equal(7, files.Sum(f => f.Length));
    }

    [Fact]
    public void Subdirectories_skip_junctions_and_missing_paths_are_empty()
    {
        string root = fx.Dir("profiles");
        fx.Dir(@"profiles\Default");
        fx.Dir("elsewhere");
        fx.Junction(@"profiles\Profile 9", Path.Combine(fx.Root, "elsewhere"));
        var tree = new FileSystemTree();

        Assert.Equal([Path.Combine(root, "Default")], tree.Subdirectories(root));
        Assert.Empty(tree.Files(Path.Combine(fx.Root, "missing"), TestContext.Current.CancellationToken));
        Assert.False(tree.Exists(Path.Combine(fx.Root, "missing")));
    }
}
