using System.Diagnostics;
using System.Runtime.InteropServices;
using Spacewise.Core;

namespace Spacewise.Platform.Linux;

public sealed class LinuxScanner : IScanner
{
    public Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Требуется Linux.");
        return Task.Run(() => Scan(request, progress, cancellation), CancellationToken.None);
    }

    private static ScanResult Scan(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellation)
    {
        string root = LinuxPaths.Normalize(request.Root);
        if (request.MaximumFiles < 1 || request.MaximumDirectoryDepth < 1) throw new ArgumentOutOfRangeException(nameof(request));
        string[] exclusions = request.Exclusions.Select(LinuxPaths.Normalize).ToArray();
        bool Excluded(string path) => exclusions.Any(p => LinuxPaths.Under(path, p))
            || new[] { "/proc", "/sys", "/dev" }.Any(p => LinuxPaths.Under(path, p));
        long files = 0, bytes = 0, skipped = 0, errors = 0;
        bool stopped = false, truncated = false;
        var diagnostics = new List<string>();
        var top = new PriorityQueue<FileFinding, long>();
        var folderTotals = new Dictionary<string, (long Files, long Bytes)>(StringComparer.Ordinal);
        var pending = new Stack<(string Path, int Depth)>();
        var clock = Stopwatch.StartNew();
        pending.Push((root, 0));
        if (!Directory.Exists(root)) return new(Guid.NewGuid(), root, ScanState.RootNotFound, 0, 0, 0, 0, [], []);
        while (pending.TryPop(out var next))
        {
            if (cancellation.IsCancellationRequested || files >= request.MaximumFiles) { stopped = true; break; }
            if (Excluded(next.Path)) { skipped++; continue; }
            try
            {
                using var directory = LinuxNative.OpenDirectory(next.Path);
                // Enumerate the anchored directory, even if its original name is replaced concurrently.
                foreach (string entry in Directory.EnumerateFileSystemEntries("/proc/self/fd/" + LinuxNative.Fd(directory)))
                {
                    if (cancellation.IsCancellationRequested || files >= request.MaximumFiles) { stopped = true; break; }
                    string name = Path.GetFileName(entry);
                    string path = Path.Combine(next.Path, name);
                    if (Excluded(path)) { skipped++; continue; }
                    try
                    {
                        LinuxNative.Stat stat = LinuxNative.Read(LinuxNative.Fd(directory), name);
                        if (stat.Type == LinuxNative.DirectoryType)
                        {
                            if (next.Depth < request.MaximumDirectoryDepth && pending.Count < 100_000) pending.Push((path, next.Depth + 1));
                            else { skipped++; truncated = true; }
                        }
                        else if (stat.Type == LinuxNative.Regular)
                        {
                            long size = checked((long)stat.Size);
                            files++;
                            bytes = checked(bytes + size);
                            var finding = new FileFinding(path, size, checked((long)stat.Blocks * 512), stat.Identity, checked((int)stat.Links));
                            top.Enqueue(finding, size);
                            if (top.Count > 100) top.Dequeue();
                            string? folder = Path.GetDirectoryName(path);
                            for (int depth = 0; depth < 256 && folder is not null && folder != root && LinuxPaths.Under(folder, root); depth++)
                            {
                                if (folderTotals.TryGetValue(folder, out var total) || folderTotals.Count < 4096)
                                    folderTotals[folder] = (total.Files + 1, total.Bytes + size);
                                else truncated = true;
                                folder = Path.GetDirectoryName(folder);
                            }
                        }
                        else skipped++; // symlinks, sockets, devices and FIFOs are metadata only.
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors++; }
                    if (clock.ElapsedMilliseconds >= 200)
                    {
                        progress?.Report(new(files, bytes, skipped, errors));
                        clock.Restart();
                    }
                }
                if (stopped) break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors++;
                if (diagnostics.Count < 10) diagnostics.Add(e.GetType().Name + ": " + e.Message);
            }
        }
        ScanState state = cancellation.IsCancellationRequested ? ScanState.Cancelled : stopped || errors > 0 || truncated ? ScanState.Partial : ScanState.Completed;
        return new(Guid.NewGuid(), root, state, files, bytes, skipped, errors,
            top.UnorderedItems.Select(p => p.Element).OrderByDescending(p => p.LogicalBytes).ToArray(), diagnostics)
        {
            Folders = folderTotals.Select(p => new FolderAggregate(p.Key, p.Value.Files, p.Value.Bytes)).OrderByDescending(p => p.LogicalBytes).ToArray(),
            FolderAggregationTruncated = truncated, SkippedByPolicy = skipped, StoppedEarly = stopped,
            UnvisitedDirectories = pending.Count,
        };
    }
}
