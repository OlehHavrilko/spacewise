using System.Runtime.InteropServices;
using Spacewise.Core;

namespace Spacewise.Platform.Windows;

public sealed class ShellRecycleBin : IRecycleBin
{
    private const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;
    private const int EmptyAlready = unchecked((int)0x8000FFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo
    {
        // SHQUERYRBINFO order: cbSize, i64Size (bytes), i64NumItems.
        public uint Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref QueryInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr window, string? rootPath, uint flags);

    public RecycleBinInfo? Query()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var info = new QueryInfo { Size = (uint)Marshal.SizeOf<QueryInfo>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? new(info.Items, info.Bytes) : null;
    }

    public bool Empty()
    {
        if (!OperatingSystem.IsWindows()) return false;
        int result = SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        // Windows reports an already empty bin as a failure code; for the user that is success.
        return result == 0 || result == EmptyAlready;
    }
}
