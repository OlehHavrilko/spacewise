using Spacewise.Core;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

public sealed class RestartManagerTests : IDisposable
{
    private readonly FsFixture fx = new();

    public void Dispose() => fx.Dispose();

    [Fact]
    public void Open_file_is_reported_with_its_process()
    {
        string path = fx.File("locked.bin", 10);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            LockingProcess holder = Assert.Single(new RestartManager().WhoLocks([path]));
            Assert.Equal(Environment.ProcessId, holder.ProcessId);
        }
        Assert.Empty(new RestartManager().WhoLocks([path]));
    }

    [Fact]
    public void Missing_and_empty_inputs_report_nobody()
    {
        Assert.Empty(new RestartManager().WhoLocks([]));
        Assert.Empty(new RestartManager().WhoLocks([Path.Combine(fx.Root, "nope.bin")]));
    }

    [Fact]
    public void Process_list_sees_the_test_host()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.True(new ProcessList().IsRunning(self.ProcessName + ".exe"));
        Assert.False(new ProcessList().IsRunning("spacewise-no-such-process.exe"));
    }
}
