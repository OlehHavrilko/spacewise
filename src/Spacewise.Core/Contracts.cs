namespace Spacewise.Core;

public enum ScanState { Idle, Scanning, Completed, Partial, Cancelled, Failed, RootNotFound }
public sealed record ScanRequest(string Root, IReadOnlyList<string> Exclusions, int MaximumFiles = 500_000,
    int MaximumDirectoryDepth = 256);
// Roots is null until the scan has run for 2 s (FR-SCAN-004); it is sent with each later report.
public sealed record ScanProgress(long Files, long LogicalBytes, long Skipped, long Errors, IReadOnlyList<RootAggregate>? Roots = null);
// Totals per top-level folder of the scan root; files directly in the root have an empty name.
public sealed record RootAggregate(string Name, long Files, long LogicalBytes);
// Volume serial plus 128-bit NTFS/ReFS file ID; equal identities are the same file (hard links).
public readonly record struct FileIdentity(ulong VolumeSerial, UInt128 FileId);
public sealed record FileFinding(string Path, long LogicalBytes, long? AllocatedBytes = null,
    FileIdentity? Identity = null, int? Links = null)
{
    // Removing one name of a multiply linked file frees no space.
    public bool IsHardLinked => Links > 1;
}
public sealed record ScanResult(Guid ScanId, string Root, ScanState State, long Files, long LogicalBytes,
    long Skipped, long Errors, IReadOnlyList<FileFinding> LargestFiles, IReadOnlyList<string> Diagnostics)
{
    public long SkippedByPolicy { get; init; }
    public long UnvisitedDirectories { get; init; }
    // Cancellation or the file limit left an unknown remainder unvisited.
    public bool StoppedEarly { get; init; }
    // Coverage buckets (FR-SCAN-002): excluded by rules, inaccessible (ACL/IO), not scanned.
    public long ExcludedByPolicy => SkippedByPolicy;
    public long Inaccessible => Errors;
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

public sealed record PolicyDecision(bool Allowed, string Reason);

// Plan-level gate in front of the executor: a plan is runnable only if it is non-empty, unmodified and unexpired.
// Per-item rules (protected paths, known action) are enforced again by PlanExecutor and by handle in the remover.
public static class MutationPolicy
{
    public static PolicyDecision Evaluate(CleanupPlan plan, DateTime nowUtc)
    {
        if (plan.Items.Count == 0) return new(false, "В плане нет файлов.");
        if (!plan.DigestMatches()) return new(false, "План изменён: digest не совпал.");
        if (nowUtc >= plan.ExpiresUtc) return new(false, "План устарел, постройте его заново.");
        return new(true, "");
    }
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
