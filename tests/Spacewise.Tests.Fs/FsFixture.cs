using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Spacewise.Tests.Fs;

// A unique directory on the fixture volume. SPACEWISE_FS_ROOT points at a mounted test VHDX
// (see Mount-NtfsFixture.ps1); without it the fixture lives under %TEMP%.
// Cleanup removes only the entries this fixture created, in reverse order: no recursive deletion.
public sealed class FsFixture : IDisposable
{
    private readonly List<(string Path, bool IsDirectory)> created = [];

    public FsFixture()
    {
        string? configured = Environment.GetEnvironmentVariable("SPACEWISE_FS_ROOT");
        string baseRoot = string.IsNullOrWhiteSpace(configured) ? Path.GetTempPath() : configured;
        Root = Path.Combine(baseRoot, "SpacewiseFs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public bool IsNtfs => new DriveInfo(Path.GetPathRoot(Root)!).DriveFormat == "NTFS";

    public string Dir(string relative)
    {
        string path = Path.Combine(Root, relative);
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new InvalidOperationException("Create the parent first: " + path);
        Directory.CreateDirectory(path);
        created.Add((path, true));
        return path;
    }

    public string File(string relative, int bytes)
    {
        string path = Path.Combine(Root, relative);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        created.Add((path, false));
        return path;
    }

    public string HardLink(string relative, string existing)
    {
        string path = Path.Combine(Root, relative);
        if (!CreateHardLinkW(path, existing, IntPtr.Zero)) throw new IOException("CreateHardLink failed: " + Marshal.GetLastPInvokeError());
        created.Add((path, false));
        return path;
    }

    // Sparse file with no allocated ranges: large logical size, near-zero size on disk.
    public string SparseFile(string relative, long length)
    {
        string path = Path.Combine(Root, relative);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            created.Add((path, false));
            if (!DeviceIoControl(stream.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException("FSCTL_SET_SPARSE failed: " + Marshal.GetLastPInvokeError());
            stream.SetLength(length);
        }
        return path;
    }

    public void Dispose()
    {
        // Undo attribute experiments (e.g. Offline) before deleting.
        System.IO.File.SetAttributes(Root, FileAttributes.Normal);
        for (int i = created.Count - 1; i >= 0; i--)
        {
            var (path, isDirectory) = created[i];
            if (isDirectory) Directory.Delete(path);
            else System.IO.File.Delete(path);
        }
        Directory.Delete(Root);
    }

    private const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize,
        IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
}
