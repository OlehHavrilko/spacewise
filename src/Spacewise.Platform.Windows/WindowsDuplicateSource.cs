using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Spacewise.Core;

namespace Spacewise.Platform.Windows;

public sealed class WindowsDuplicateSource : IDuplicateSource
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    public IEnumerable<DuplicateCandidate> Candidates(string root, IReadOnlyList<string> exclusions, CancellationToken cancellation)
    {
        root = ScanPaths.Normalize(root);
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal)
            || new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network) throw new ArgumentException("Нужна папка на локальном диске.");
        foreach (FileEntry entry in new FileSystemTree().Files(root, cancellation))
        {
            if (MetadataScanPolicy.ShouldSkip(entry.Attributes) || ScanPaths.IsExcluded(entry.Path, exclusions)) continue;
            FileDetails? metadata = FileMetadata.TryRead(entry.Path);
            if (metadata is { Links: 1, Identity: { } id }) yield return new(entry.Path, entry.Length, entry.LastWriteUtc, id);
        }
    }

    public DuplicateRead Open(DuplicateCandidate candidate)
    {
        // No write/delete sharing while hashing. Open the reparse point itself and never recall cloud content.
        var fd = CreateFileW(@"\\?\" + candidate.Path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000 | 0x00100000 | 0x08000000, IntPtr.Zero);
        try
        {
            var final = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandleW(fd, final, (uint)final.Capacity, 0);
            if (length == 0 || length >= final.Capacity || !string.Equals(final.ToString(), @"\\?\" + candidate.Path, StringComparison.OrdinalIgnoreCase)
                || !Matches(fd, candidate)) throw new IOException("Файл изменился или является ссылкой.");
            var stream = new FileStream(fd, FileAccess.Read);
            return new(stream, () => Matches(fd, candidate));
        }
        catch { fd.Dispose(); throw; }
    }

    private static bool Matches(SafeFileHandle fd, DuplicateCandidate file) => !fd.IsInvalid
        && !MetadataScanPolicy.ShouldSkip(File.GetAttributes(fd)) && RandomAccess.GetLength(fd) == file.Size
        && File.GetLastWriteTimeUtc(fd) == file.MtimeUtc && FileMetadata.TryRead(fd) is { Links: 1, Identity: { } id } && id == file.Identity;
}
