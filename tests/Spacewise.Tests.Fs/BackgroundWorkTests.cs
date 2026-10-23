using System.Runtime.InteropServices;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

public class BackgroundWorkTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadInformation(IntPtr thread, int informationClass, ref PowerThrottlingState state, int size);

    private static uint ThrottleState()
    {
        var state = new PowerThrottlingState { Version = 1 };
        Assert.True(GetThreadInformation(GetCurrentThread(), 3, ref state, Marshal.SizeOf<PowerThrottlingState>()), "GetThreadInformation error " + Marshal.GetLastWin32Error());
        return state.StateMask;
    }

    [Fact]
    public async Task Worker_thread_is_ecoqos_inside_the_scope_and_back_to_default_after_it()
    {
        // A thread pool thread keeps its state when it returns to the pool, so the scope must undo it.
        (uint before, uint during, uint after, bool background, bool throttled) = await Task.Run(() =>
        {
            uint before = ThrottleState();
            using BackgroundWork work = BackgroundWork.Enter();
            uint during = ThrottleState();
            work.Dispose();
            return (before, during, ThrottleState(), work.IsBackground, work.IsThrottled);
        }, TestContext.Current.CancellationToken);

        Assert.True(background);
        Assert.True(throttled);
        Assert.Equal(0u, before);
        Assert.Equal(1u, during);
        Assert.Equal(0u, after);
    }
}
