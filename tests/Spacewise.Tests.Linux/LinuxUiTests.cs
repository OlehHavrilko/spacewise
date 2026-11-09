using Avalonia;
using Avalonia.Headless;
using Spacewise.Linux;
using Spacewise.Platform.Linux;

namespace Spacewise.Tests.Linux;

public sealed class LinuxUiTests
{
    [Fact]
    public void Avalonia_window_builds_and_lays_out_without_windows_dependencies()
    {
        AppBuilder.Configure<LinuxApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var window = new MainWindow(new("/home/test", "/home/test/.cache", "/home/test/.local/share/spacewise"));
        window.Measure(new Size(1060, 820)); window.Arrange(new Rect(0, 0, 1060, 820));
        Assert.Equal("Spacewise — Linux", window.Title);
        Assert.True(window.Bounds.Width > 0);
        Assert.DoesNotContain(typeof(MainWindow).Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "Spacewise.Platform.Windows");
        window.Close();
    }
}
