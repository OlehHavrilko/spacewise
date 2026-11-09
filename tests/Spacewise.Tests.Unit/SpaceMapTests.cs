using Spacewise.Core;

namespace Spacewise.Tests.Unit;

public sealed class SpaceMapTests
{
    [Fact]
    public void Folder_totals_include_descendants_without_counting_root_files_as_a_folder()
    {
        string root = Path.GetFullPath("fixture");
        var folders = new FolderAccumulator(root);
        folders.Add(Path.Combine(root, "a", "b", "c", "d", "one.bin"), 70);
        folders.Add(Path.Combine(root, "a", "two.bin"), 20);
        folders.Add(Path.Combine(root, "root.bin"), 10);
        var snapshot = folders.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(90, snapshot.Single(f => f.Path == Path.Combine(root, "a")).LogicalBytes);
        Assert.Equal(2, snapshot.Single(f => f.Path == Path.Combine(root, "a")).Files);
        Assert.Equal(70, snapshot.Single(f => f.Path == Path.Combine(root, "a", "b", "c")).LogicalBytes);
        Assert.False(folders.Truncated);
    }

    [Fact]
    public void A_million_files_do_not_grow_the_aggregate_beyond_its_node_budget()
    {
        string root = Path.GetFullPath("fixture");
        var folders = new FolderAccumulator(root, maximumFolders: 16);
        for (int i = 0; i < 1_000_000; i++) folders.Add(Path.Combine(root, $"folder{i % 100}", "one.bin"), 1);
        Assert.True(folders.Truncated);
        Assert.Equal(16, folders.Snapshot().Count);
        Assert.All(folders.Snapshot(), f => Assert.Equal(10_000, f.LogicalBytes));
    }

    [Theory]
    [InlineData(800, 250)]
    [InlineData(100, 900)]
    [InlineData(0, 0)]
    public void Map_areas_match_weights_and_do_not_overlap(double width, double height)
    {
        double[] weights = [70, 20, 10, 0];
        var rectangles = TreemapLayout.Arrange(weights, width, height);
        for (int i = 0; i < rectangles.Count; i++)
        {
            var r = rectangles[i];
            Assert.InRange(r.X + r.Width, 0, width + 0.001);
            Assert.InRange(r.Y + r.Height, 0, height + 0.001);
            Assert.Equal(width * height * weights[i] / 100, r.Width * r.Height, precision: 5);
            for (int j = 0; j < i; j++)
            {
                var s = rectangles[j];
                double overlap = Math.Max(0, Math.Min(r.X + r.Width, s.X + s.Width) - Math.Max(r.X, s.X))
                    * Math.Max(0, Math.Min(r.Y + r.Height, s.Y + s.Height) - Math.Max(r.Y, s.Y));
                Assert.InRange(overlap, 0, 0.001);
            }
        }
    }

    [Fact]
    public void Empty_and_zero_weight_maps_are_empty()
    {
        Assert.Empty(TreemapLayout.Arrange([], 200, 100));
        Assert.All(TreemapLayout.Arrange([0, -1, double.NaN], 200, 100), r => Assert.Equal(0, r.Width * r.Height));
    }
}
