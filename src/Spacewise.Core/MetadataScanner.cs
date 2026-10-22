using System.Diagnostics;

namespace Spacewise.Core;

public sealed class MetadataScanner : IScanner
{
    public Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellation)
        => Task.Run(() => Scan(request, progress, cancellation));

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
        var stack = new Stack<(string Path, IEnumerator<string> Entries)>();
        long files = 0, bytes = 0, skipped = 0, errors = 0;
        long skippedByPolicy = 0, unvisitedDirectories = 0;
        ScanState state = ScanState.Completed;
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
        void PushDirectory(string path)
        {
            if (MetadataScanPolicy.ShouldSkip(File.GetAttributes(path))) { PolicySkip(); return; }
            if (stack.Count >= request.MaximumDirectoryDepth)
            {
                skipped++; unvisitedDirectories++; state = ScanState.Partial;
                if (diagnostics.Count < 50) diagnostics.Add($"Лимит глубины: каталог не проверен: {path}");
                return;
            }
            stack.Push((path, Directory.EnumerateFileSystemEntries(path).GetEnumerator()));
        }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            CheckAncestors();
            if (ScanPaths.IsExcluded(root, excluded)) PolicySkip();
            else PushDirectory(root);
            while (stack.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var frame = stack.Peek();
                string? path;
                try
                {
                    if (!frame.Entries.MoveNext())
                    {
                        stack.Pop().Entries.Dispose();
                        continue;
                    }
                    path = frame.Entries.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Problem(frame.Path, ex); stack.Pop().Entries.Dispose(); continue;
                }
                cancellation.ThrowIfCancellationRequested();
                if (ScanPaths.IsExcluded(path, excluded)) { PolicySkip(); continue; }
                try
                {
                    FileAttributes attrs = File.GetAttributes(path);
                    if (MetadataScanPolicy.ShouldSkip(attrs)) { PolicySkip(); continue; }
                    if ((attrs & FileAttributes.Directory) != 0) PushDirectory(path);
                    else
                    {
                        // Only an additional eligible file proves a file-limit coverage gap.
                        if (files >= request.MaximumFiles)
                        {
                            state = ScanState.Partial;
                            diagnostics.Add("Достигнут лимит файлов; остались непроверенные файлы.");
                            goto Finished;
                        }
                        long length = new FileInfo(path).Length;
                        bytes = checked(bytes + length);
                        files++;
                        top.Enqueue(new FileFinding(path, length), length);
                        if (top.Count > 100) top.Dequeue();
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
        catch (OperationCanceledException) { state = ScanState.Cancelled; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { Problem(root, ex); state = ScanState.Failed; }
        finally { while (stack.Count > 0) stack.Pop().Entries.Dispose(); }
    Finished:
        if (state == ScanState.Completed && errors > 0) state = ScanState.Partial;
        progress?.Report(new(files, bytes, skipped, errors));
        return new(Guid.NewGuid(), root, state, files, bytes, skipped, errors,
            top.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.LogicalBytes).ToArray(), diagnostics)
        { SkippedByPolicy = skippedByPolicy, UnvisitedDirectories = unvisitedDirectories };
    }
}
