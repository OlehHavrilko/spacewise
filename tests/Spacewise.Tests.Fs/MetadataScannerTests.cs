using Spacewise.Core;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

// Former SmokeTests harness (smoke and --regression) on a real file system.
public sealed class MetadataScannerTests : IDisposable
{
    private readonly FsFixture fx = new();
    private readonly MetadataScanner scanner = new();
    private readonly string keep, excluded;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public MetadataScannerTests()
    {
        keep = fx.File("keep.bin", 17);
        excluded = fx.Dir("excluded");
        fx.File(Path.Combine("excluded", "other.bin"), 41);
    }

    public void Dispose() => fx.Dispose();

    private Task<ScanResult> Scan(string? root = null, string[]? exclusions = null, int maxFiles = 500_000, int maxDepth = 256,
        CancellationToken? cancellation = null) =>
        scanner.ScanAsync(new(root ?? fx.Root, exclusions ?? [], maxFiles, maxDepth), null, cancellation ?? Ct);

    [Fact]
    public async Task Totals_are_complete_with_empty_coverage_buckets()
    {
        var r = await Scan();
        Assert.Equal(ScanState.Completed, r.State);
        Assert.Equal(2, r.Files);
        Assert.Equal(58, r.LogicalBytes);
        Assert.Equal(0, r.ExcludedByPolicy);
        Assert.Equal(0, r.Inaccessible);
        Assert.Equal(0, r.UnvisitedDirectories);
        Assert.False(r.StoppedEarly);
    }

    [Fact]
    public async Task Scan_preserves_fixture_files()
    {
        await Scan();
        Assert.Equal(17, new FileInfo(keep).Length);
        Assert.Equal(2, Directory.GetFiles(fx.Root, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Exclusion_completes_selected_scope()
    {
        var r = await Scan(exclusions: [excluded]);
        Assert.Equal(ScanState.Completed, r.State);
        Assert.Equal(1, r.Files);
        Assert.Equal(17, r.LogicalBytes);
        Assert.Equal(1, r.ExcludedByPolicy);
    }

    [Fact]
    public async Task Exclusion_accepts_alternate_casing()
    {
        var r = await Scan(exclusions: [excluded.ToUpperInvariant()]);
        Assert.Equal(ScanState.Completed, r.State);
        Assert.Equal(1, r.Files);
    }

    [Fact]
    public async Task Root_exclusion_with_trailing_separator_skips_one_subtree()
    {
        var r = await Scan(exclusions: [fx.Root + Path.DirectorySeparatorChar]);
        Assert.Equal(ScanState.Completed, r.State);
        Assert.Equal(0, r.Files);
        Assert.Equal(1, r.ExcludedByPolicy);
        Assert.Equal(1, r.Skipped);
    }

    [Fact]
    public async Task Volume_root_exclusion_covers_descendants()
    {
        var r = await Scan(exclusions: [Path.GetPathRoot(fx.Root)!]);
        Assert.Equal(0, r.Files);
        Assert.Equal(1, r.ExcludedByPolicy);
    }

    [Fact]
    public async Task Copy_as_path_quotes_are_accepted()
    {
        var r = await Scan(root: '"' + fx.Root + '"');
        Assert.Equal(ScanState.Completed, r.State);
        Assert.Equal(58, r.LogicalBytes);
    }

    [Fact]
    public async Task Pre_cancelled_scan_stops_early()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await Scan(cancellation: cts.Token);
        Assert.Equal(ScanState.Cancelled, r.State);
        Assert.Equal(0, r.Files);
        Assert.True(r.StoppedEarly);
    }

    [Fact]
    public async Task File_limit_with_remaining_files_is_partial()
    {
        var r = await Scan(maxFiles: 1);
        Assert.Equal(ScanState.Partial, r.State);
        Assert.Equal(1, r.Files);
        Assert.True(r.StoppedEarly);
    }

    [Fact]
    public async Task Exact_file_limit_is_complete_when_nothing_remains()
    {
        var r = await Scan(maxFiles: 2);
        Assert.Equal(ScanState.Completed, r.State);
        Assert.Equal(2, r.Files);
        Assert.False(r.StoppedEarly);
    }

    [Fact]
    public async Task Missing_root_reports_RootNotFound()
    {
        var r = await Scan(root: Path.Combine(fx.Root, "missing"));
        Assert.Equal(ScanState.RootNotFound, r.State);
        Assert.Equal(0, r.Errors);
        Assert.Contains(r.Diagnostics, x => x.Contains("не найдена"));
        Assert.DoesNotContain(r.Diagnostics, x => x.Contains("Корень и его родители"));
    }

    [Fact]
    public async Task Relative_root_is_rejected() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Scan(root: "relative"));

    [Fact]
    public async Task Offline_root_is_rejected_before_enumeration()
    {
        FileAttributes initial = File.GetAttributes(fx.Root);
        try
        {
            File.SetAttributes(fx.Root, initial | FileAttributes.Offline);
            Assert.True((File.GetAttributes(fx.Root) & FileAttributes.Offline) != 0, "offline flag applied");
            var r = await Scan();
            Assert.Equal(ScanState.Failed, r.State);
            Assert.Equal(0, r.Files);
        }
        finally { File.SetAttributes(fx.Root, initial); }
    }

    [Fact]
    public async Task Depth_limit_reports_unvisited_subtree_and_normal_depth_resumes()
    {
        fx.Dir(Path.Combine("excluded", "level1"));
        fx.Dir(Path.Combine("excluded", "level1", "level2"));
        var limited = await Scan(maxDepth: 2);
        Assert.Equal(ScanState.Partial, limited.State);
        Assert.Equal(1, limited.UnvisitedDirectories);
        Assert.Contains(limited.Diagnostics, x => x.Contains("Лимит глубины"));
        var nested = await Scan();
        Assert.Equal(ScanState.Completed, nested.State);
        Assert.Equal(0, nested.UnvisitedDirectories);
    }

    [Fact]
    public async Task Wide_directory_with_10002_children_is_fully_traversed()
    {
        for (int i = 0; i < 10_002; i++) fx.Dir("wide-" + i);
        var r = await Scan();
        Assert.Equal(ScanState.Completed, r.State);
        Assert.Equal(0, r.Skipped);
        Assert.Equal(2, r.Files);
    }
}
