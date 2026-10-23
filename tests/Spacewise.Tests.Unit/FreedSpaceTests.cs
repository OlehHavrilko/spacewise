using Spacewise.Core;

namespace Spacewise.Tests.Unit;

public class FreedSpaceTests
{
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1000, 1100)]
    [InlineData(1000, 900)]
    public void Numbers_within_ten_percent_need_no_explanation(long estimated, long measured) =>
        Assert.Null(FreedSpace.WhyDifferent(estimated, measured));

    [Fact]
    public void Smaller_gain_names_hard_links_sparse_files_and_other_writers()
    {
        string? why = FreedSpace.WhyDifferent(1000, 500);
        Assert.NotNull(why);
        Assert.Contains("жёсткими ссылками", why);
        Assert.Contains("разреженные", why);
    }

    [Fact]
    public void Larger_gain_names_clusters()
    {
        string? why = FreedSpace.WhyDifferent(1000, 1500);
        Assert.NotNull(why);
        Assert.Contains("кластеров", why);
    }

    [Fact]
    public void Negative_measurement_says_free_space_dropped()
    {
        string? why = FreedSpace.WhyDifferent(1000, -50);
        Assert.NotNull(why);
        Assert.Contains("меньше", why);
    }

    [Fact]
    public void No_measurement_or_no_estimate_has_nothing_to_explain()
    {
        Assert.Null(FreedSpace.WhyDifferent(1000, null));
        Assert.Null(FreedSpace.WhyDifferent(0, 100));
    }
}
