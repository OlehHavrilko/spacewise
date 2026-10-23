using System.Diagnostics;
using System.IO.Enumeration;
using Spacewise.Core;

namespace Spacewise.Platform.Windows;

public sealed class MetadataScanner : IScanner
{
    // Attributes and length come from the enumeration buffer (FR-SCAN-001): no per-entry syscalls.
    private readonly record struct Entry(string Path, FileAttributes Attributes, long Length);
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false,
        ReturnSpecialDirectories = false, BufferSize = 65536,
    };
    private static IEnumerator<Entry> Enumerate(string path) => new FileSystemEnumerable<Entry>(path,
        (ref FileSystemEntry e) => new Entry(e.ToFullPath(), e.Attributes, e.Length), Options).GetEnumerator();
    private const int TopCount = 100;

    public Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellation)
        => Task.Run(() =>
        {
            using BackgroundWork background = BackgroundWork.Enter();
            return Scan(request, progress, cancellation);
        });

    private static ScanResult Scan(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellation)
    {
        string root = ScanPaths.Normalize(request.Root);
        if (!Path.IsPathFullyQualified(ScanPaths.Unquote(request.Root)) || root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Выберите абсолютный путь на локальном диске.");
        if (request.MaximumFiles < 1) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.MaximumDirectoryDepth < 1) throw new ArgumentOutOfRangeException(nameof(request));
        if (MetadataScanPolicy.IsNetwork(new DriveInfo(Path.GetPathRoot(root)!).DriveType))
            throw new ArgumentException("Подключённые сетевые диски не поддерживаются этим каркасом.");
        string[] excluded = request.Exclusions.Where(x => !string.IsNullOrWhiteSpace(x)).Select(ScanPaths.Normalize).ToArray();
        var diagnostics = new List<string>();
        var top = new PriorityQueue<FileFinding, long>();
        var stack = new Stack<(string Path, IEnumerator<Entry> Entries)>();
        long files = 0, bytes = 0, skipped = 0, errors = 0;
        long skippedByPolicy = 0, unvisitedDirectories = 0;
        ScanState state = ScanState.Completed;
        bool stoppedEarly = false;
        var clock = Stopwatch.StartNew();
        void Problem(string path, Exception ex)
        {
            errors++;
            if (diagnostics.Count < 50) diagnostics.Add($"{path}: {ex.Message}");
        }
        void CheckAncestors()
        {
            var ancestors = new Stack<string>();
            for (DirectoryInfo? d = new(root); d != null; d = d.Parent)
                ancestors.Push(d.FullName);
            foreach (string path in ancestors)
                if (MetadataScanPolicy.ShouldSkip(File.GetAttributes(path)))
                    throw new ArgumentException("Корень и его родители не должны быть ссылками, Offline или cloud-recall объектами.");
        }
        void PolicySkip() { skipped++; skippedByPolicy++; }
        void PushDirectory(string path, FileAttributes attributes)
        {
            if (MetadataScanPolicy.ShouldSkip(attributes)) { PolicySkip(); return; }
            if (stack.Count >= request.MaximumDirectoryDepth)
            {
                skipped++; unvisitedDirectories++; state = ScanState.Partial;
                if (diagnostics.Count < 50) diagnostics.Add($"Лимит глубины: каталог не проверен: {path}");
                return;
            }
            stack.Push((path, Enumerate(path)));
        }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            FileAttributes rootAttributes;
            try { rootAttributes = File.GetAttributes(root); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                state = ScanState.RootNotFound;
                diagnostics.Add($"Папка не найдена: {root}");
                goto Finished;
            }
            CheckAncestors();
            if (ScanPaths.IsExcluded(root, excluded)) PolicySkip();
            else PushDirectory(root, rootAttributes);
            while (stack.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var frame = stack.Peek();
                Entry entry;
                try
                {
                    if (!frame.Entries.MoveNext())
                    {
                        stack.Pop().Entries.Dispose();
                        continue;
                    }
                    entry = frame.Entries.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Problem(frame.Path, ex); stack.Pop().Entries.Dispose(); continue;
                }
                cancellation.ThrowIfCancellationRequested();
                string path = entry.Path;
                if (ScanPaths.IsExcluded(path, excluded)) { PolicySkip(); continue; }
                try
                {
                    if (MetadataScanPolicy.ShouldSkip(entry.Attributes)) { PolicySkip(); continue; }
                    if ((entry.Attributes & FileAttributes.Directory) != 0) PushDirectory(path, entry.Attributes);
                    else
                    {
                        // Only an additional eligible file proves a file-limit coverage gap.
                        if (files >= request.MaximumFiles)
                        {
                            state = ScanState.Partial; stoppedEarly = true;
                            diagnostics.Add("Достигнут лимит файлов; остались непроверенные файлы.");
                            goto Finished;
                        }
                        long length = entry.Length;
                        bytes = checked(bytes + length);
                        files++;
                        top.Enqueue(new FileFinding(path, length), length);
                        if (top.Count > TopCount) top.Dequeue();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Problem(path, ex); }
                if (clock.ElapsedMilliseconds >= 250)
                {
                    progress?.Report(new(files, bytes, skipped, errors));
                    clock.Restart();
                }
            }
        }
        catch (OperationCanceledException) { state = ScanState.Cancelled; stoppedEarly = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { Problem(root, ex); state = ScanState.Failed; }
        finally { while (stack.Count > 0) stack.Pop().Entries.Dispose(); }
    Finished:
        if (state == ScanState.Completed && errors > 0) state = ScanState.Partial;
        progress?.Report(new(files, bytes, skipped, errors));
        // One handle query per top-N file only; skipped after cancellation to keep Stop fast.
        IEnumerable<FileFinding> largest = top.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.LogicalBytes);
        if (state != ScanState.Cancelled) largest = largest.Select(FileMetadata.Enrich);
        return new(Guid.NewGuid(), root, state, files, bytes, skipped, errors, largest.ToArray(), diagnostics)
        { SkippedByPolicy = skippedByPolicy, UnvisitedDirectories = unvisitedDirectories, StoppedEarly = stoppedEarly };
    }
}
