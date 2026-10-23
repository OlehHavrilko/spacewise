using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Spacewise.Core;

namespace Spacewise.Platform.Windows;

// Reversible removal: the verified file is renamed into the quarantine directory through the same handle
// (NtSetInformationFile, ADR 0002 step 4). Same volume only; across volumes the rename fails and nothing is deleted.
public sealed unsafe partial class QuarantineFileRemover(string quarantineDirectory) : HandleFileRemover, IFileRestorer, IQuarantinePurger
{
    private const uint AddFile = 0x2;
    private const uint RenamePosix = 0x2;
    private const int RenameInformationEx = 65;
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034), StatusNotSameDevice = unchecked((int)0xC00000D4);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle OpenFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSetInformationFile(SafeFileHandle file, IoStatus* status, void* information, uint length, int informationClass);

    protected override RemoveResult Act(SafeFileHandle child, PlanItem item)
    {
        Directory.CreateDirectory(quarantineDirectory);
        using SafeFileHandle target = OpenFile(@"\\?\" + quarantineDirectory, ListDirectory | ReadAttributes | Synchronize, ShareAll, 0,
            OpenExisting, BackupSemantics | OpenReparse, 0);
        if (target.IsInvalid) return new(RemoveStatus.Failed, Marshal.GetLastPInvokeError());
        if (!IsExpectedDirectory(target, quarantineDirectory)) return new(RemoveStatus.Unsafe);

        string stored = Guid.NewGuid().ToString("N");
        int status = Rename(child, target, stored);
        if (status == 0)
        {
            string storedPath = Path.Combine(quarantineDirectory, stored);
            return new(RemoveStatus.Removed, 0, storedPath, Hash(storedPath));
        }
        return new(status == StatusObjectNameNotFound ? RemoveStatus.Gone : RemoveStatus.Failed, (int)RtlNtStatusToDosError(status));
    }

    // Hash of the content as quarantined (spec §8). Null when it cannot be read; the move itself already succeeded.
    private static byte[]? Hash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return System.Security.Cryptography.SHA256.HashData(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // Irreversible. Refuses anything that is not a direct child of the quarantine directory.
    public RemoveResult Purge(string storedPath)
    {
        if (!string.Equals(Path.GetDirectoryName(storedPath), Path.TrimEndingDirectorySeparator(quarantineDirectory), StringComparison.OrdinalIgnoreCase))
            return new(RemoveStatus.Unsafe);
        return OpenAndAct(storedPath, DeleteByHandle);
    }

    // Puts a quarantined file back under its original name. Never replaces an existing file.
    public RestoreResult Restore(string storedPath, string originalPath)
    {
        string? originalParent = Path.GetDirectoryName(originalPath), name = Path.GetFileName(originalPath);
        if (string.IsNullOrEmpty(originalParent) || string.IsNullOrEmpty(name)) return new(false);
        if (!string.Equals(Path.GetDirectoryName(storedPath), Path.TrimEndingDirectorySeparator(quarantineDirectory), StringComparison.OrdinalIgnoreCase))
            return new(false);
        using SafeFileHandle destination = OpenFile(@"\\?\" + originalParent, ListDirectory | ReadAttributes | Synchronize, ShareAll, 0,
            OpenExisting, BackupSemantics | OpenReparse, 0);
        if (destination.IsInvalid) return new(false, Marshal.GetLastPInvokeError());
        if (!IsExpectedDirectory(destination, originalParent)) return new(false);

        using SafeFileHandle stored = OpenFile(@"\\?\" + storedPath, Delete | Synchronize, ShareAll, 0, OpenExisting, OpenReparse, 0);
        if (stored.IsInvalid) return new(false, Marshal.GetLastPInvokeError());
        int status = Rename(stored, destination, name);
        return new(status == 0, status == 0 ? 0 : (int)RtlNtStatusToDosError(status));
    }

    // FILE_RENAME_INFORMATION_EX (x64): Flags@0, RootDirectory@8, FileNameLength@16, FileName@20.
    // Flags without REPLACE_IF_EXISTS: an existing name is STATUS_OBJECT_NAME_COLLISION.
    private static int Rename(SafeFileHandle file, SafeFileHandle root, string name)
    {
        int nameBytes = name.Length * 2, size = 24 + nameBytes;
        byte* buffer = stackalloc byte[size];
        new Span<byte>(buffer, size).Clear();
        *(uint*)buffer = RenamePosix;
        *(nint*)(buffer + 8) = root.DangerousGetHandle();
        *(uint*)(buffer + 16) = (uint)nameBytes;
        fixed (char* chars = name) Buffer.MemoryCopy(chars, buffer + 20, nameBytes, nameBytes);
        IoStatus io;
        return NtSetInformationFile(file, &io, buffer, (uint)size, RenameInformationEx);
    }
}
