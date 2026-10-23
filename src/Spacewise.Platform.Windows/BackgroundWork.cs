using System.Runtime.InteropServices;

namespace Spacewise.Platform.Windows;

// NFR-PERF-013: scanner workers run as EcoQoS (power throttling) and in background mode (low CPU, memory and I/O priority)
// so a scan does not compete with the user's foreground work. The UI thread never enters this mode.
// A thread pool thread must leave the mode before it returns to the pool, hence the IDisposable.
public sealed class BackgroundWork : IDisposable
{
    private const int ThreadPowerThrottling = 3;
    private const uint ThrottlingVersion = 1, ThrottlingExecutionSpeed = 1;
    private const int ModeBegin = 0x00010000, ModeEnd = 0x00020000;
    private readonly bool backgroundEntered;
    private readonly bool throttled;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadInformation(IntPtr thread, int informationClass, ref PowerThrottlingState state, int size);

    private BackgroundWork(bool backgroundEntered, bool throttled)
    {
        this.backgroundEntered = backgroundEntered;
        this.throttled = throttled;
    }

    public bool IsBackground => backgroundEntered;
    public bool IsThrottled => throttled;

    // Best effort: on a system that refuses either call the scan still runs, just without that hint.
    public static BackgroundWork Enter()
    {
        if (!OperatingSystem.IsWindows()) return new(false, false);
        IntPtr thread = GetCurrentThread();
        bool background = SetThreadPriority(thread, ModeBegin);
        var state = new PowerThrottlingState { Version = ThrottlingVersion, ControlMask = ThrottlingExecutionSpeed, StateMask = ThrottlingExecutionSpeed };
        bool eco = SetThreadInformation(thread, ThreadPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
        return new(background, eco);
    }

    public void Dispose()
    {
        IntPtr thread = GetCurrentThread();
        if (throttled)
        {
            // ControlMask set with StateMask clear returns the thread to the system default.
            var state = new PowerThrottlingState { Version = ThrottlingVersion, ControlMask = ThrottlingExecutionSpeed, StateMask = 0 };
            SetThreadInformation(thread, ThreadPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
        }
        if (backgroundEntered) SetThreadPriority(thread, ModeEnd);
    }
}
