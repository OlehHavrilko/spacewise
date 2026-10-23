using System.Diagnostics;

namespace Spacewise.Tests.Fs;

// NFR-PERF-010 / A3: closing the window leaves no Spacewise process after 5 s. NFR-PERF-011: no service or autostart entry.
// FR-DATA-004: the log is written and holds no file paths at Information level.
public sealed class DesktopProcessTests
{
    private static string? DesktopExe()
    {
        // tests\Spacewise.Tests.Fs\bin\<config>\net10.0-windows\ -> repository root is five levels up.
        string baseDir = AppContext.BaseDirectory.TrimEnd('\\');
        string tfm = Path.GetFileName(baseDir);
        string config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        string root = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        string exe = Path.Combine(root, "src", "Spacewise.Desktop", "bin", config, tfm, "Spacewise.Desktop.exe");
        return File.Exists(exe) ? exe : null;
    }

    [Fact]
    public void Window_close_ends_every_spacewise_process_within_five_seconds_and_the_log_has_no_paths()
    {
        string? exe = DesktopExe();
        if (exe is null) { Assert.Skip("Spacewise.Desktop is not built for this configuration."); return; }
        string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spacewise", "logs");
        DateTime startedAt = DateTime.UtcNow;

        using Process app = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (app.MainWindowHandle == IntPtr.Zero && !app.HasExited && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
            app.Refresh();
        }
        Assert.NotEqual(IntPtr.Zero, app.MainWindowHandle);

        Assert.True(app.CloseMainWindow());
        bool exited = app.WaitForExit(5000);
        if (!exited) app.Kill();
        Assert.True(exited, "the process did not exit within 5 s of closing the window");
        Assert.DoesNotContain(Process.GetProcessesByName("Spacewise.Desktop"), p => p.StartTime.ToUniversalTime() >= startedAt.AddSeconds(-1));

        string log = Directory.GetFiles(logs, "spacewise-*.log").Select(f => new FileInfo(f)).OrderBy(f => f.LastWriteTimeUtc).Last().FullName;
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        string text = new StreamReader(stream).ReadToEnd();
        Assert.Contains("Spacewise started", text);
        Assert.DoesNotContain(@":\", text);
    }

    [Fact]
    public void No_service_and_no_run_key_for_spacewise_exist()
    {
        foreach (string hive in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(hive);
            Assert.DoesNotContain(key?.GetValueNames() ?? [], n => n.Contains("Spacewise", StringComparison.OrdinalIgnoreCase));
        }
        using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        Assert.DoesNotContain(services!.GetSubKeyNames(), n => n.Contains("Spacewise", StringComparison.OrdinalIgnoreCase));
    }
}
