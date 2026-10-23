using Spacewise.Core;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

// FR-SCAN-003: size on disk, file ID and link count for top-N. Needs NTFS (a mounted VHDX or an NTFS %TEMP%).
public sealed class NtfsMetadataTests : IDisposable
{
    private readonly FsFixture fx = new();
    private readonly MetadataScanner scanner = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public NtfsMetadataTests()
    {
        if (fx.IsNtfs) return;
        fx.Dispose(); // xUnit does not dispose an instance whose constructor skipped.
        Assert.Skip("Нужен том NTFS: задайте SPACEWISE_FS_ROOT.");
    }

    public void Dispose() => fx.Dispose();

    private Task<ScanResult> Scan() => scanner.ScanAsync(new(fx.Root, []), null, Ct);

    [Fact]
    public async Task Top_files_carry_allocated_size_link_count_and_distinct_ids()
    {
        fx.File("a.bin", 17);
        fx.File("b.bin", 70_000);
        var r = await Scan();
        Assert.All(r.LargestFiles, f =>
        {
            Assert.NotNull(f.AllocatedBytes);
            Assert.Equal(1, f.Links);
            Assert.NotNull(f.Identity);
            Assert.False(f.IsHardLinked);
        });
        Assert.Equal(2, r.LargestFiles.Select(f => f.Identity).Distinct().Count());
        // Non-resident data is allocated in whole clusters.
        Assert.True(r.LargestFiles.Single(f => f.LogicalBytes == 70_000).AllocatedBytes >= 70_000);
    }

    [Fact]
    public async Task Hard_link_names_share_one_identity_and_are_marked()
    {
        string original = fx.File("keep.bin", 17);
        fx.HardLink("link.bin", original);
        var r = await Scan();
        Assert.Equal(2, r.Files);
        Assert.Equal(34, r.LogicalBytes); // logical sum still counts both names
        Assert.All(r.LargestFiles, f => { Assert.True(f.IsHardLinked); Assert.Equal(2, f.Links); });
        Assert.Single(r.LargestFiles.Select(f => f.Identity).Distinct());
    }

    [Fact]
    public async Task Sparse_file_reports_size_on_disk_below_logical_size()
    {
        const long length = 64L << 20;
        fx.SparseFile("sparse.bin", length);
        var finding = Assert.Single((await Scan()).LargestFiles);
        Assert.Equal(length, finding.LogicalBytes);
        Assert.True(finding.AllocatedBytes < length / 16, $"allocated {finding.AllocatedBytes}");
    }

    [Fact]
    public void Missing_file_has_no_details() => Assert.Null(FileMetadata.TryRead(Path.Combine(fx.Root, "missing.bin")));
}
