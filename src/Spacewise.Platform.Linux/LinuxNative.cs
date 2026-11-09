using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Spacewise.Core;

namespace Spacewise.Platform.Linux;

internal static class LinuxNative
{
    internal const int DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000;
    internal const int PathFlag = 0x200000;
    internal const int AtEmptyPath = 0x1000, AtNoFollow = 0x100;
    internal const uint Regular = 0x8000, DirectoryType = 0x4000;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct Stat
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(112)] public long MtimeSeconds;
        [FieldOffset(120)] public uint MtimeNanos;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        public readonly uint Type => (uint)Mode & 0xf000;
        public readonly FileIdentity Identity => new(((ulong)DeviceMajor << 32) | DeviceMinor, Inode);
        public readonly DateTime Mtime => DateTimeOffset.FromUnixTimeSeconds(MtimeSeconds).UtcDateTime.AddTicks(MtimeNanos / 100);
    }

    [DllImport("libc", SetLastError = true)] internal static extern int openat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode = 0);
    [DllImport("libc", SetLastError = true)] private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Stat stat);
    [DllImport("libc", SetLastError = true)] internal static extern int renameat2(int fromfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int tofd, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
    [DllImport("libc", SetLastError = true)] internal static extern int unlinkat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] internal static extern int mkdirat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", SetLastError = true)] internal static extern int fsync(int fd);
    [DllImport("libc")] internal static extern uint geteuid();
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileSystemStat { [FieldOffset(0)] public long Type; }
    [DllImport("libc", SetLastError = true)] private static extern int fstatfs(int fd, out FileSystemStat stat);
    // Content analysis is limited to known local filesystems; FUSE/cloud/network mounts are excluded.
    internal static bool IsLocal(int fd) => fstatfs(fd, out var stat) == 0
        && stat.Type is 0xef53 or 0x58465342 or 0x9123683e or 0x01021994 or 0x794c7630;

    internal static SafeFileHandle Handle(int fd)
    {
        if (fd < 0) throw new IOException("Linux filesystem error: " + Marshal.GetLastPInvokeError());
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }
    internal static int Fd(SafeFileHandle handle) => handle.DangerousGetHandle().ToInt32();
    internal static Stat Read(int fd, string path = "")
    {
        if (statx(fd, path, path.Length == 0 ? AtEmptyPath : AtNoFollow, 0x7ff, out Stat stat) != 0)
            throw new IOException("statx error: " + Marshal.GetLastPInvokeError());
        if ((stat.Mask & 0x74f) != 0x74f) throw new IOException("Файловая система не вернула обязательные метаданные.");
        return stat;
    }

    // Anchor every component by descriptor. No ancestor or leaf symlink is followed.
    internal static SafeFileHandle OpenDirectory(string path, bool create = false)
    {
        string normalized = LinuxPaths.Normalize(path);
        SafeFileHandle current = Handle(openat(-100, "/", DirectoryFlag | NoFollow | CloseOnExec));
        try
        {
            foreach (string part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (create && mkdirat(Fd(current), part, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17)
                    throw new IOException("mkdirat error: " + Marshal.GetLastPInvokeError());
                SafeFileHandle next = Handle(openat(Fd(current), part, DirectoryFlag | NoFollow | CloseOnExec));
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    internal static void RequirePrivate(SafeFileHandle directory)
    {
        Stat stat = Read(Fd(directory));
        if (stat.Uid != geteuid() || (stat.Mode & 0x3f) != 0)
            throw new IOException("Каталог данных должен принадлежать текущему пользователю и иметь права 0700.");
    }

    internal static void Sync(SafeFileHandle directory)
    {
        if (fsync(Fd(directory)) != 0) throw new IOException("fsync error: " + Marshal.GetLastPInvokeError());
    }
}
