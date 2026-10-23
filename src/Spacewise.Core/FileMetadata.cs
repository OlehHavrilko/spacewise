using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Spacewise.Core;

public sealed record FileDetails(long AllocatedBytes, int Links, FileIdentity? Identity);

// Per-file handle queries, used only for top-N candidates (FR-SCAN-003), never for every entry.
// The handle asks for FILE_READ_ATTRIBUTES only and opens the reparse point itself, so it does not
// read content, follow links, or trigger cloud recall.
public static class FileMetadata
{
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileStandardInfoClass = 1;
    private const int FileIdInfoClass = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass,
        out FileStandardInfo info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass,
        out FileIdInfo info, int size);

    // Null when the file is gone, inaccessible, or the platform is not Windows.
    public static FileDetails? TryRead(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using SafeFileHandle handle = CreateFileW(@"\\?\" + path, FileReadAttributes, ShareAll, IntPtr.Zero,
            OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        if (!GetFileInformationByHandleEx(handle, FileStandardInfoClass, out FileStandardInfo standard,
                Marshal.SizeOf<FileStandardInfo>()))
            return null;
        // FileIdInfo is unavailable on FAT/exFAT; size and link count are still valid there.
        FileIdentity? identity = GetFileInformationByHandleEx(handle, FileIdInfoClass, out FileIdInfo id,
            Marshal.SizeOf<FileIdInfo>())
            ? new FileIdentity(id.VolumeSerialNumber, new UInt128(id.FileIdHigh, id.FileIdLow))
            : null;
        return new FileDetails(standard.AllocationSize, checked((int)standard.NumberOfLinks), identity);
    }

    public static FileFinding Enrich(FileFinding finding) => TryRead(finding.Path) is { } d
        ? finding with { AllocatedBytes = d.AllocatedBytes, Links = d.Links, Identity = d.Identity }
        : finding;
}
