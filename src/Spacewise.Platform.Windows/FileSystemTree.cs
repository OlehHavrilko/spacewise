using System.IO.Enumeration;
using Spacewise.Core;

namespace Spacewise.Platform.Windows;

// IFileTree over the real file system. Reparse points (junctions, symlinks, cloud placeholders)
// are reported as entries but never entered, so a junction cannot lead the preview outside its root.
public sealed class FileSystemTree : IFileTree
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = true, ReturnSpecialDirectories = false,
    };

    public bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    public IEnumerable<string> Subdirectories(string path) => Safe(() =>
        new FileSystemEnumerable<string>(path, (ref FileSystemEntry e) => e.ToFullPath(), Options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) == 0,
        });

    public IEnumerable<FileEntry> Files(string root, CancellationToken cancellation)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            var entries = Safe(() => new FileSystemEnumerable<(FileEntry File, bool Descend)>(dir, (ref FileSystemEntry e) =>
                (new FileEntry(e.ToFullPath(), e.Length, e.Attributes, e.LastWriteTimeUtc.UtcDateTime),
                 e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) == 0), Options));
            foreach ((FileEntry file, bool descend) in entries)
            {
                if (descend) pending.Push(file.Path);
                else if ((file.Attributes & FileAttributes.Directory) == 0) yield return file;
            }
        }
    }

    // A directory that vanished or became inaccessible is skipped. FileSystemEnumerable opens the
    // directory in its constructor, so creation is guarded as well as MoveNext.
    private static IEnumerable<T> Safe<T>(Func<IEnumerable<T>> open)
    {
        IEnumerator<T> e;
        try { e = open().GetEnumerator(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        using (e)
            while (true)
            {
                try { if (!e.MoveNext()) yield break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
                yield return e.Current;
            }
    }
}
