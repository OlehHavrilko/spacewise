using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spacewise.Core;

namespace Spacewise.Platform.Windows;

// Restart Manager (rstrtmgr.dll): lists the processes that have the given files open.
// Read-only: the session never calls RmShutdown or RmRestart.
public sealed class RestartManager : ILockInspector
{
    private const int ErrorMoreData = 234;

    public IReadOnlyList<LockingProcess> WhoLocks(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return [];
        var key = new StringBuilder(33);
        int rc = RmStartSession(out uint session, 0, key);
        if (rc != 0) throw new InvalidOperationException($"RmStartSession: {rc}");
        try
        {
            rc = RmRegisterResources(session, (uint)paths.Count, [.. paths], 0, IntPtr.Zero, 0, null);
            if (rc != 0) throw new InvalidOperationException($"RmRegisterResources: {rc}");
            uint count = 0;
            RM_PROCESS_INFO[] info = [];
            while (true)
            {
                rc = RmGetList(session, out uint needed, ref count, info, out _);
                if (rc == 0) break;
                if (rc != ErrorMoreData) throw new InvalidOperationException($"RmGetList: {rc}");
                info = new RM_PROCESS_INFO[needed];
                count = needed;
            }
            return [.. info.Take((int)count).Select(p => new LockingProcess(p.Process.dwProcessId, p.strAppName))
                .DistinctBy(p => p.ProcessId)];
        }
        finally { RmEndSession(session); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public uint StartTimeLow, StartTimeHigh;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint applications,
        IntPtr uniqueProcesses, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[] info, out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}

public sealed class ProcessList : IProcessList
{
    public bool IsRunning(string imageName)
    {
        Process[] found = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(imageName));
        foreach (Process p in found) p.Dispose();
        return found.Length > 0;
    }
}
