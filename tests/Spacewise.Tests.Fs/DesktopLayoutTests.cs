using System.Diagnostics;

namespace Spacewise.Tests.Fs;

[Collection("Desktop app")]
public sealed class DesktopLayoutTests
{
    [Fact]
    public async Task Navigation_screens_and_selection_work_at_scaled_viewports_and_system_palette()
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd('\\');
        string exe = Path.Combine(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..")), "src", "Spacewise.Desktop", "bin",
            Path.GetFileName(Path.GetDirectoryName(baseDir)!), Path.GetFileName(baseDir), "Spacewise.Desktop.exe");
        Assert.True(File.Exists(exe), "Build Spacewise.Desktop before running layout regression checks.");
        using Process app = Process.Start(new ProcessStartInfo(exe, "--verify-layout")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        try
        {
            Task<string> output = app.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Task<string> error = app.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await app.WaitForExitAsync(timeout.Token);
            Assert.True(app.ExitCode == 0, await output + await error);
            Assert.Contains("PASS: 144 layout cases", await output);
        }
        finally { if (!app.HasExited) app.Kill(entireProcessTree: true); }
    }
}
