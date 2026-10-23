using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

public class ShellRecycleBinTests
{
    // Query only: emptying the real Recycle Bin of whoever runs the tests is never part of a test.
    [Fact]
    public void Query_answers_with_non_negative_numbers()
    {
        var info = new ShellRecycleBin().Query();
        Assert.NotNull(info);
        Assert.True(info.Items >= 0);
        Assert.True(info.Bytes >= 0);
        Assert.True(info.Items > 0 || info.Bytes == 0);
    }
}
