using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Spacewise.Core;

namespace Spacewise.Platform.Windows;

// ADR 0002: open the parent, check it is the directory the plan names, open the child relative to that handle
// (NtCreateFile, no path re-resolution), compare with the plan, delete through the same handle with POSIX semantics.
public unsafe partial class HandleFileRemover : IFileRemover
{
    protected const uint Delete = 0x00010000, Synchronize = 0x00100000, ListDirectory = 0x1, ReadAttributes = 0x80;
    protected const uint ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparse = 0x00200000;
    private const uint FileOpen = 1, SynchronousIo = 0x20, NonDirectory = 0x40, CaseInsensitive = 0x40;
    protected const int BasicInfo = 0, StandardInfo = 1, DispositionInfoEx = 21;
    private const uint DeleteFlag = 0x1, PosixFlag = 0x2, IgnoreReadOnlyFlag = 0x10;
    private const uint AttrReparse = 0x400, AttrDirectory = 0x10;
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034), StatusObjectPathNotFound = unchecked((int)0xC000003A),
        StatusSharingViolation = unchecked((int)0xC0000043);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public char* Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public uint Length; public nint Root; public UnicodeString* Name; public uint Attributes; public nint Sd, Qos; }

    [StructLayout(LayoutKind.Sequential)]
    protected struct IoStatus { public nint Status, Information; }

    [StructLayout(LayoutKind.Sequential)]
    protected struct BasicInformation { public long Creation, LastAccess, LastWrite, Change; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInformation { public long Allocation, EndOfFile; public uint Links; public byte DeletePending, Directory; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    protected static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* buffer, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetFinalPathName(SafeFileHandle file, char* buffer, uint size, uint flags);

    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(nint* handle, uint access, ObjectAttributes* attributes, IoStatus* status, long* allocation,
        uint fileAttributes, uint share, uint disposition, uint options, void* ea, uint eaLength);

    [LibraryImport("ntdll.dll")]
    protected static partial uint RtlNtStatusToDosError(int status);

    public RemoveResult Remove(PlanItem item) => OpenAndAct(item.Path, child => Process(child, item));

    // Opens the file relative to a verified parent handle and hands the handle to `act`.
    protected RemoveResult OpenAndAct(string path, Func<SafeFileHandle, RemoveResult> act)
    {
        if (!OperatingSystem.IsWindows()) return new(RemoveStatus.Failed);
        string? parentPath = Path.GetDirectoryName(path), name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(name)) return new(RemoveStatus.Unsafe);

        using SafeFileHandle parent = CreateFile(@"\\?\" + parentPath, ListDirectory | ReadAttributes | Synchronize, ShareAll, 0,
            OpenExisting, BackupSemantics | OpenReparse, 0);
        if (parent.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            return new(error is 2 or 3 ? RemoveStatus.Gone : RemoveStatus.Failed, error);
        }
        // A junction or symlink anywhere above resolves to a different final path than the plan names.
        if (!IsExpectedDirectory(parent, parentPath)) return new(RemoveStatus.Unsafe);

        fixed (char* chars = name)
        {
            var objectName = new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = chars };
            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes), Root = parent.DangerousGetHandle(), Name = &objectName, Attributes = CaseInsensitive,
            };
            nint raw;
            IoStatus io;
            int status = NtCreateFile(&raw, Delete | ReadAttributes | Synchronize, &attributes, &io, null, 0, ShareAll, FileOpen,
                OpenReparse | SynchronousIo | NonDirectory, null, 0);
            if (status != 0)
            {
                int code = (int)RtlNtStatusToDosError(status);
                return new(status is StatusObjectNameNotFound or StatusObjectPathNotFound ? RemoveStatus.Gone
                    : status == StatusSharingViolation ? RemoveStatus.Locked : RemoveStatus.Failed, code);
            }
            using var child = new SafeFileHandle(raw, ownsHandle: true);
            return act(child);
        }
    }

    private RemoveResult Process(SafeFileHandle child, PlanItem item)
    {
        BasicInformation basic;
        StandardInformation standard;
        if (!GetFileInformationByHandleEx(child, BasicInfo, &basic, (uint)sizeof(BasicInformation))
            || !GetFileInformationByHandleEx(child, StandardInfo, &standard, (uint)sizeof(StandardInformation)))
            return new(RemoveStatus.Failed, Marshal.GetLastPInvokeError());
        if ((basic.Attributes & (AttrReparse | AttrDirectory)) != 0) return new(RemoveStatus.Unsafe);
        if (standard.EndOfFile != item.ExpectedSize || DateTime.FromFileTimeUtc(basic.LastWrite) != item.ExpectedMtimeUtc)
            return new(RemoveStatus.Changed);
        return Act(child, item);
    }

    // The child is verified against the plan; subclasses decide what happens to it.
    protected virtual RemoveResult Act(SafeFileHandle child, PlanItem item) => DeleteByHandle(child);

    protected static RemoveResult DeleteByHandle(SafeFileHandle child)
    {
        // IGNORE_READONLY: without it a read-only file is ACCESS_DENIED (ADR 0002, step 3).
        uint* info = stackalloc uint[1];
        *info = DeleteFlag | PosixFlag | IgnoreReadOnlyFlag;
        if (SetFileInformationByHandle(child, DispositionInfoEx, info, sizeof(uint))) return new(RemoveStatus.Removed);
        int error = Marshal.GetLastPInvokeError();
        return new(error is 32 or 33 ? RemoveStatus.Locked : RemoveStatus.Failed, error);
    }

    protected static bool IsExpectedDirectory(SafeFileHandle handle, string expected)
    {
        BasicInformation basic;
        if (!GetFileInformationByHandleEx(handle, BasicInfo, &basic, (uint)sizeof(BasicInformation))) return false;
        if ((basic.Attributes & AttrReparse) != 0 || (basic.Attributes & AttrDirectory) == 0) return false;
        char* buffer = stackalloc char[1024];
        uint length = GetFinalPathName(handle, buffer, 1024, 0);
        if (length == 0 || length >= 1024) return false;
        string actual = new(buffer, 0, (int)length);
        return string.Equals(actual, @"\\?\" + Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase);
    }
}
