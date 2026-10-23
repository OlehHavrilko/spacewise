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
    public void Mutations_are_denied_and_model_is_disabled()
    {
        Assert.False(ReadOnlyPolicy.EvaluateMutation().Allowed);
        Assert.False(new DisabledLocalAssistant().IsAvailable);
    }
}
