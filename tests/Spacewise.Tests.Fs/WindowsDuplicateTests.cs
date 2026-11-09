using Spacewise.Core;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

public sealed class WindowsDuplicateTests
{
    [Fact]
    public async Task Real_content_is_grouped_and_changed_file_cannot_be_opened_as_old_candidate()
    {
        using var fx = new FsFixture();
        string a = fx.File("a.bin", 8), b = fx.File("b.bin", 8);
        var source = new WindowsDuplicateSource();
        var result = await new DuplicateFinder(source).FindAsync(fx.Root, [], TestContext.Current.CancellationToken);
        Assert.Equal([a, b], Assert.Single(result.Groups).Files.Select(f => f.Path).Order().ToArray());
        DuplicateCandidate old = source.Candidates(fx.Root, [], TestContext.Current.CancellationToken).First(f => f.Path == a);
        File.WriteAllText(a, "changed size");
        Assert.Throws<IOException>(() => source.Open(old));
    }
}
