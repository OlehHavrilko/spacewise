namespace Cleaner.Core;

public enum ScanState { Idle, Scanning, Completed, Partial, Cancelled, Failed }
public sealed record ScanRequest(string Root, IReadOnlyList<string> Exclusions, int MaximumFiles = 500_000,
    int MaximumDirectoryDepth = 256);
public sealed record ScanProgress(long Files, long LogicalBytes, long Skipped, long Errors);
public sealed record FileFinding(string Path, long LogicalBytes, long? AllocatedBytes = null);
public sealed record ScanResult(Guid ScanId, string Root, ScanState State, long Files, long LogicalBytes,
    long Skipped, long Errors, IReadOnlyList<FileFinding> LargestFiles, IReadOnlyList<string> Diagnostics)
{
    public long SkippedByPolicy { get; init; }
    public long UnvisitedDirectories { get; init; }
}

public interface IScanner
{
    Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellation);
}

public static class MetadataScanPolicy
{
    private const FileAttributes DeferredOrLink = FileAttributes.ReparsePoint | FileAttributes.Offline
        | (FileAttributes)0x40000 | (FileAttributes)0x400000;
    public static bool ShouldSkip(FileAttributes attributes) => (attributes & DeferredOrLink) != 0;
    public static bool IsNetwork(DriveType type) => type == DriveType.Network;
}

// Conservative user exclusions: ignore case even on case-sensitive Windows directories.
// This can exclude an extra case-variant sibling, but never widens the requested scan.
public static class ScanPaths
{
    public static string Unquote(string path) => path.Trim().Trim('"');
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Unquote(path)));
    public static bool IsExcluded(string path, IEnumerable<string> exclusions) => exclusions.Any(x =>
        string.Equals(Path.TrimEndingDirectorySeparator(path), x, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(x) ? x : x + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
}

// Deliberately no execution interface, shell command, or Delete operation in this starter.
public sealed record PolicyDecision(bool Allowed, string Reason);
public static class ReadOnlyPolicy
{
    public static PolicyDecision EvaluateMutation() => new(false, "Каркас поддерживает только анализ.");
}

public sealed class DisabledLocalAssistant
{
    public bool IsAvailable => false;
    public string Explain() => "Локальная модель не подключена. Доступен обычный анализ метаданных.";
}

public sealed class ScanSession
{
    public ScanState State { get; private set; } = ScanState.Idle;
    public void Start()
    {
        if (State == ScanState.Scanning) throw new InvalidOperationException("Проверка уже выполняется.");
        State = ScanState.Scanning;
    }
    public void Finish(ScanState result)
    {
        if (State != ScanState.Scanning || result is ScanState.Idle or ScanState.Scanning)
            throw new InvalidOperationException("Недопустимый переход состояния.");
        State = result;
    }
}
