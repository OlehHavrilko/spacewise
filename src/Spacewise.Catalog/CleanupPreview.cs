using Spacewise.Core;

namespace Spacewise.Catalog;

public sealed record PreviewItem(string Path, long LogicalBytes, DateTime LastWriteUtc);

// Estimate only (FR-CLN-002): logical bytes of matched files, not what the volume will gain.
public sealed record RulePreview(CleanupRule Rule, IReadOnlyList<PreviewItem> Items, long TooRecent, long Protected, long Deferred)
{
    public long LogicalBytes => Items.Sum(i => i.LogicalBytes);
    // Rule's runningProcess entries that are running now.
    public IReadOnlyList<string> Running { get; init; } = [];
    // FR-CLN-014: processes holding one of the first LockChecked items open.
    public IReadOnlyList<LockingProcess> LockedBy { get; init; } = [];
    public int LockChecked { get; init; }
}

// FR-CLN-013: preview is the selection the executor will run, without mutation.
public sealed class CleanupPreviewer(CleanupCatalog catalog, IFileTree tree, UserFolders folders, TimeProvider time,
    IProcessList? processes = null, ILockInspector? locks = null)
{
    // Restart Manager cost grows with registered files; larger rules are sampled.
    public const int MaximumLockCheck = 2000;

    public IReadOnlyList<RulePreview> Preview(CancellationToken cancellation, Func<CleanupRule, bool>? only = null)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        var results = new List<RulePreview>();
        foreach (CleanupRule rule in catalog.Rules)
        {
            cancellation.ThrowIfCancellationRequested();
            if (only is not null && !only(rule)) continue;
            if (!rule.Detect.AnyPath.Any(p => tree.Exists(Expand(p)))) continue;
            results.Add(PreviewRule(rule, now, cancellation));
        }
        return results;
    }

    private RulePreview PreviewRule(CleanupRule rule, DateTime now, CancellationToken cancellation)
    {
        var items = new List<PreviewItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long tooRecent = 0, denied = 0, deferred = 0;
        foreach (RuleTarget target in rule.Targets)
        {
            Glob[] include = [.. target.Include.Select(p => new Glob(p))];
            foreach (string root in Roots(rule, target))
            {
                if (!tree.Exists(root)) continue;
                foreach (FileEntry file in tree.Files(root, cancellation))
                {
                    if (!ProtectedPaths.IsUnder(file.Path, root) || file.Path.Length <= root.Length + 1) continue;
                    string relative = file.Path[(root.Length + 1)..];
                    if (!include.Any(g => g.IsMatch(relative)) || !seen.Add(file.Path)) continue;
                    if (MetadataScanPolicy.ShouldSkip(file.Attributes)) { deferred++; continue; }
                    if (!ProtectedPaths.Check(file.Path, folders).Allowed) { denied++; continue; }
                    if (target.MinAgeDays > 0 && file.LastWriteUtc > now.AddDays(-target.MinAgeDays)) { tooRecent++; continue; }
                    items.Add(new(file.Path, file.Length, file.LastWriteUtc));
                }
            }
        }
        string[] running = processes is null ? [] : [.. (rule.RunningProcess ?? []).Where(processes.IsRunning)];
        string[] sample = [.. items.Take(MaximumLockCheck).Select(i => i.Path)];
        IReadOnlyList<LockingProcess> lockedBy = locks is null || sample.Length == 0 ? [] : locks.WhoLocks(sample);
        return new(rule, items, tooRecent, denied, deferred) { Running = running, LockedBy = lockedBy, LockChecked = locks is null ? 0 : sample.Length };
    }

    private IEnumerable<string> Roots(CleanupRule rule, RuleTarget target)
    {
        if (!target.Root.StartsWith("{profile}", StringComparison.Ordinal))
        {
            yield return Expand(target.Root);
            yield break;
        }
        string profiles = Expand(rule.Profiles!.Root);
        if (!tree.Exists(profiles)) yield break;
        Glob[] names = [.. rule.Profiles.Include.Select(p => new Glob(p))];
        string rest = target.Root["{profile}".Length..];
        foreach (string dir in tree.Subdirectories(profiles))
            if (names.Any(g => g.IsMatch(Path.GetFileName(dir)))) yield return dir + rest;
    }

    private string Expand(string template)
    {
        int end = template.IndexOf('%', 1);
        string value = template[1..end].ToUpperInvariant() switch
        {
            "USERPROFILE" => folders.Profile,
            "LOCALAPPDATA" => folders.LocalAppData,
            "APPDATA" => folders.RoamingAppData,
            "TEMP" => folders.Temp,
            "SYSTEMROOT" => folders.SystemRoot,
            var other => throw new CatalogException($"Неизвестная переменная %{other}%."),
        };
        return Path.TrimEndingDirectorySeparator(value) + template[(end + 1)..];
    }
}
