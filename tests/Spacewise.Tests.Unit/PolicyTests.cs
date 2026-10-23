using Spacewise.Core;

namespace Spacewise.Tests.Unit;

public class PolicyTests
{
    [Theory]
    [InlineData(FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.Offline)]
    [InlineData((FileAttributes)0x40000)]
    [InlineData((FileAttributes)0x400000)]
    public void Each_link_offline_or_recall_flag_is_skipped(FileAttributes flag)
    {
        Assert.True(MetadataScanPolicy.ShouldSkip(flag));
        Assert.True(MetadataScanPolicy.ShouldSkip(flag | FileAttributes.Archive));
    }

    [Fact]
    public void Ordinary_archive_and_read_only_entries_stay_eligible() =>
        Assert.False(MetadataScanPolicy.ShouldSkip(FileAttributes.Archive | FileAttributes.ReadOnly | FileAttributes.Hidden));

    [Fact]
    public void Network_drives_are_rejected()
    {
        Assert.True(MetadataScanPolicy.IsNetwork(DriveType.Network));
        Assert.False(MetadataScanPolicy.IsNetwork(DriveType.Fixed));
    }

    [Theory]
    [InlineData(@"C:\Data\Cache", @"C:\Data\Cache", true)]
    [InlineData(@"C:\Data\Cache\a.bin", @"C:\Data\Cache", true)]
    [InlineData(@"C:\DATA\cache\a.bin", @"C:\Data\Cache", true)]
    [InlineData(@"C:\Data\Cache-sibling", @"C:\Data\Cache", false)]
    [InlineData(@"C:\Data\Cache\", @"C:\Data\Cache", true)]
    [InlineData(@"C:\Data\x", @"C:\", true)]
    public void Exclusion_respects_path_boundaries(string path, string exclusion, bool excluded) =>
        Assert.Equal(excluded, ScanPaths.IsExcluded(path, [exclusion]));

    [Fact]
    public void Copy_as_path_quotes_are_removed() =>
        Assert.Equal(@"C:\Data", ScanPaths.Normalize("  \"C:\\Data\\\"  "));

    [Fact]
    public void Model_is_disabled() => Assert.False(new DisabledLocalAssistant().IsAvailable);

    [Fact]
    public void Mutation_policy_allows_only_a_non_empty_intact_fresh_plan()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var item = new PlanItem("r", 1, "delete", @"C:\Users\u\AppData\Local\Temp\a", 1, now);
        CleanupPlan plan = CleanupPlan.Create([item], now, TimeSpan.FromHours(1), "user");

        Assert.True(MutationPolicy.Evaluate(plan, now.AddMinutes(1)).Allowed);
        Assert.False(MutationPolicy.Evaluate(CleanupPlan.Create([], now, TimeSpan.FromHours(1), "user"), now).Allowed);
        Assert.False(MutationPolicy.Evaluate(plan, now.AddHours(1)).Allowed);
        Assert.False(MutationPolicy.Evaluate(plan with { Items = [item with { ExpectedSize = 2 }] }, now).Allowed);
    }
}
