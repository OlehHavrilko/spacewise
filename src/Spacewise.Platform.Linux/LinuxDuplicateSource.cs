using Spacewise.Core;

namespace Spacewise.Platform.Linux;

public sealed class LinuxDuplicateSource : IDuplicateSource
{
    public IEnumerable<DuplicateCandidate> Candidates(string root, IReadOnlyList<string> exclusions, CancellationToken cancellation)
    {
        root = LinuxPaths.Normalize(root);
        string[] excluded = exclusions.Select(LinuxPaths.Normalize).Concat(["/proc", "/sys", "/dev"]).ToArray();
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.TryPop(out var next))
        {
            cancellation.ThrowIfCancellationRequested();
            if (excluded.Any(p => LinuxPaths.Under(next.Path, p))) continue;
            using var dir = TryDirectory(next.Path);
            if (dir is null) continue;
            foreach (string entry in Directory.EnumerateFileSystemEntries("/proc/self/fd/" + LinuxNative.Fd(dir)))
            {
                cancellation.ThrowIfCancellationRequested();
                string name = Path.GetFileName(entry), path = Path.Combine(next.Path, name);
                if (excluded.Any(p => LinuxPaths.Under(path, p))) continue;
                LinuxNative.Stat? possible = TryStat(LinuxNative.Fd(dir), name);
                if (possible is not { } stat) continue;
                if (stat.Type == LinuxNative.DirectoryType && next.Depth < 128 && stack.Count < 100_000) stack.Push((path, next.Depth + 1));
                else if (stat.Type == LinuxNative.Regular && stat.Links == 1)
                    yield return new(path, checked((long)stat.Size), stat.Mtime, stat.Identity);
            }
        }
    }

    public DuplicateRead Open(DuplicateCandidate candidate)
    {
        using var parent = LinuxNative.OpenDirectory(Path.GetDirectoryName(candidate.Path)!);
        var fd = LinuxNative.Handle(LinuxNative.openat(LinuxNative.Fd(parent), Path.GetFileName(candidate.Path),
            LinuxNative.NoFollow | LinuxNative.CloseOnExec | 0x800));
        try
        {
            if (!LinuxNative.IsLocal(LinuxNative.Fd(fd)) || !Matches(LinuxNative.Read(LinuxNative.Fd(fd)), candidate))
                throw new IOException("Файл изменился, связан ссылками или находится на неподдерживаемой ФС.");
            var stream = new FileStream(fd, FileAccess.Read);
            return new(stream, () =>
            {
                using var currentParent = LinuxNative.OpenDirectory(Path.GetDirectoryName(candidate.Path)!);
                return Matches(LinuxNative.Read(LinuxNative.Fd(fd)), candidate)
                    && Matches(LinuxNative.Read(LinuxNative.Fd(currentParent), Path.GetFileName(candidate.Path)), candidate);
            });
        }
        catch { fd.Dispose(); throw; }
    }

    private static bool Matches(LinuxNative.Stat stat, DuplicateCandidate file) => stat.Type == LinuxNative.Regular && stat.Links == 1
        && stat.Identity == file.Identity && stat.Size == (ulong)file.Size && stat.Mtime == file.MtimeUtc;
    private static Microsoft.Win32.SafeHandles.SafeFileHandle? TryDirectory(string path)
    { try { return LinuxNative.OpenDirectory(path); } catch (IOException) { return null; } }
    private static LinuxNative.Stat? TryStat(int fd, string name)
    { try { return LinuxNative.Read(fd, name); } catch (IOException) { return null; } }
}
