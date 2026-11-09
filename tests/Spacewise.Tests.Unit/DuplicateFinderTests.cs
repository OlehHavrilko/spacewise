using Spacewise.Core;

namespace Spacewise.Tests.Unit;

public sealed class DuplicateFinderTests
{
    private sealed class Source(params (string Path, string Content, ulong Inode, bool Stable)[] files) : IDuplicateSource
    {
        public IEnumerable<DuplicateCandidate> Candidates(string root, IReadOnlyList<string> exclusions, CancellationToken cancellation) =>
            files.Select(f => new DuplicateCandidate(f.Path, System.Text.Encoding.UTF8.GetByteCount(f.Content), DateTime.UnixEpoch, new(1, f.Inode)));
        public DuplicateRead Open(DuplicateCandidate file)
        {
            var entry = files.Single(f => f.Path == file.Path);
            return new(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(entry.Content)), () => entry.Stable);
        }
    }

    [Fact]
    public async Task Same_size_different_content_and_changed_files_are_not_duplicates()
    {
        var source = new Source(("a", "same", 1, true), ("b", "same", 2, true), ("c", "diff", 3, true), ("d", "same", 4, false));
        var report = await new DuplicateFinder(source).FindAsync("/", [], TestContext.Current.CancellationToken);
        var group = Assert.Single(report.Groups);
        Assert.Equal(["a", "b"], group.Files.Select(f => f.Path)); Assert.Equal(4, group.PotentialBytes);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(Blake3.Hasher.Hash(System.Text.Encoding.UTF8.GetBytes("same")).ToString(), group.Blake3);
    }

    [Fact]
    public async Task Same_inode_is_not_counted_twice_and_byte_budget_is_explicit()
    {
        var source = new Source(("a", "same", 1, true), ("b", "same", 1, true), ("c", "same", 3, true));
        var report = await new DuplicateFinder(source).FindAsync("/", [], TestContext.Current.CancellationToken, maximumHashBytes: 4);
        Assert.Empty(report.Groups); Assert.True(report.Limited); Assert.Equal(4, report.BytesRead);
    }
}
