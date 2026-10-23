namespace Spacewise.Core;

public sealed record FileEntry(string Path, long Length, FileAttributes Attributes, DateTime LastWriteUtc);

// Read-only view of the file system for cleanup preview; the Windows implementation never follows reparse points.
public interface IFileTree
{
    bool Exists(string path);
    IEnumerable<string> Subdirectories(string path);
    // All files under root, recursively.
    IEnumerable<FileEntry> Files(string root, CancellationToken cancellation);
}

public sealed record LockingProcess(int ProcessId, string Name);

// FR-CLN-014: who holds a file open. Processes are reported, never terminated.
public interface ILockInspector
{
    IReadOnlyList<LockingProcess> WhoLocks(IReadOnlyList<string> paths);
}

public interface IProcessList
{
    // imageName as in a catalog rule, e.g. "chrome.exe".
    bool IsRunning(string imageName);
}
