using System.Runtime.InteropServices;

namespace Spacewise.Spikes.FileOps;

// Hand-written LibraryImport declarations. All pointers are blittable, no runtime marshalling.
internal static unsafe partial class Native
{
    // Access rights
    public const uint DELETE = 0x00010000;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint FILE_READ_DATA = 0x0001;
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint GENERIC_READ = 0x80000000;

    // Share
    public const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;

    // CreateFileW
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public static readonly nint INVALID_HANDLE_VALUE = -1;

    // NtCreateFile
    public const uint FILE_OPEN = 1;
    public const uint FILE_DIRECTORY_FILE = 0x00000001;
    public const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x00000020;
    public const uint FILE_NON_DIRECTORY_FILE = 0x00000040;
    public const uint FILE_OPEN_REPARSE_POINT = 0x00200000;
    public const uint OBJ_CASE_INSENSITIVE = 0x40;

    // FILE_INFO_BY_HANDLE_CLASS
    public const int FileBasicInfo = 0;
    public const int FileStandardInfo = 1;
    public const int FileDispositionInfo = 4;
    public const int FileIdInfo = 18;
    public const int FileDispositionInfoEx = 21;
    public const int FileRenameInfoEx = 22;

    // FILE_DISPOSITION_INFO_EX.Flags
    public const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    public const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;
    public const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    // FILE_RENAME_INFO(EX).Flags
    public const uint FILE_RENAME_FLAG_REPLACE_IF_EXISTS = 0x1;
    public const uint FILE_RENAME_FLAG_POSIX_SEMANTICS = 0x2;

    public const uint FILE_ATTRIBUTE_READONLY = 0x1;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OBJECT_ATTRIBUTES
    {
        public uint Length;
        public nint RootDirectory;
        public UNICODE_STRING* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_STATUS_BLOCK
    {
        public nint Status;
        public nint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public fixed byte FileId[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateFileW(char* fileName, uint access, uint share, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(nint handle, int infoClass, void* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileInformationByHandle(nint handle, int infoClass, void* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateDirectoryW(char* path, nint securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveDirectoryW(char* path);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetFileAttributesW(char* path);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateHardLinkW(char* newLink, char* existing, nint securityAttributes);

    [LibraryImport("ntdll.dll")]
    public static partial int NtCreateFile(nint* fileHandle, uint desiredAccess, OBJECT_ATTRIBUTES* objectAttributes,
        IO_STATUS_BLOCK* ioStatusBlock, long* allocationSize, uint fileAttributes, uint shareAccess,
        uint createDisposition, uint createOptions, void* eaBuffer, uint eaLength);

    // FILE_INFORMATION_CLASS (ntifs.h)
    public const int FileRenameInformationEx = 65;

    [LibraryImport("ntdll.dll")]
    public static partial int NtSetInformationFile(nint fileHandle, IO_STATUS_BLOCK* ioStatusBlock, void* fileInformation,
        uint length, int fileInformationClass);

    [LibraryImport("ntdll.dll")]
    public static partial uint RtlNtStatusToDosError(int status);
}
