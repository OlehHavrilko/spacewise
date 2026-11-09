using System.Text.Json;
using Spacewise.Core;

namespace Spacewise.Platform.Linux;

public sealed record LinuxRule(string Id, string Title, string Directory, int MinimumAgeDays, string Consequences,
    IReadOnlyList<string>? Include = null, bool UserDefined = false);
public sealed record LinuxCatalog(int Version, IReadOnlyList<LinuxRule> Rules)
{
    public static LinuxCatalog Load(string? userDirectory = null)
    {
        using Stream stream = typeof(LinuxCatalog).Assembly.GetManifestResourceStream("Spacewise.Platform.Linux.catalog.linux.json")!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 16,
        };
        LinuxCatalog builtIn = JsonSerializer.Deserialize<LinuxCatalog>(stream, options)!;
        var rules = new List<LinuxRule>(builtIn.Rules);
        if (userDirectory is not null && System.IO.Directory.Exists(userDirectory))
        {
            string[] files = System.IO.Directory.GetFiles(userDirectory, "*.json").Order(StringComparer.Ordinal).ToArray();
            if (files.Length > 32) throw new InvalidDataException("Не более 32 пользовательских каталогов.");
            foreach (string file in files)
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > 1 << 20)
                    throw new InvalidDataException("Недопустимый файл каталога.");
                using var input = File.OpenRead(file);
                LinuxCatalog catalog = JsonSerializer.Deserialize<LinuxCatalog>(input, options) ?? throw new InvalidDataException("Пустой каталог.");
                if (catalog.Version < 1 || catalog.Rules.Count > 128) throw new InvalidDataException("Недопустимая версия или число правил.");
                foreach (LinuxRule rule in catalog.Rules)
                {
                    if (rule.Id.Length is < 3 or > 100 || rule.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-')
                        || string.IsNullOrWhiteSpace(rule.Title) || string.IsNullOrWhiteSpace(rule.Consequences) || rule.MinimumAgeDays < 0
                        || rule.Directory.Split('/').Any(p => p is "" or "." or "..") || rule.Directory.IndexOfAny(['\\', '*', '?', '\0']) >= 0)
                        throw new InvalidDataException("Недопустимое пользовательское правило: " + rule.Id);
                    foreach (string pattern in rule.Include ?? ["**"]) _ = new RelativeGlob(pattern);
                    if (rule.Include is { Count: 0 or > 64 }) throw new InvalidDataException("Нужно от 1 до 64 glob-паттернов.");
                    rules.Add(rule with { UserDefined = true, Title = "[Пользовательское] " + rule.Title,
                        Consequences = "Непроверенное правило: возможна потеря данных. " + rule.Consequences });
                }
            }
        }
        if (rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rules.Count) throw new InvalidDataException("Повторяющиеся ID правил.");
        return builtIn with { Rules = rules };
    }
}

public sealed record LinuxPreview(LinuxRule Rule, IReadOnlyList<PlanItem> Items, bool Complete, long Skipped, long Errors);

public sealed class LinuxCleanupPreviewer(LinuxPaths paths, TimeProvider time)
{
    public Task<IReadOnlyList<LinuxPreview>> PreviewAsync(IReadOnlyList<string> exclusions, CancellationToken cancellation) =>
        Task.Run(() => Preview(exclusions, cancellation), cancellation);

    private IReadOnlyList<LinuxPreview> Preview(IReadOnlyList<string> exclusions, CancellationToken cancellation)
    {
        var result = new List<LinuxPreview>();
        foreach (LinuxRule rule in LinuxCatalog.Load(Path.Combine(paths.Data, "rules")).Rules)
        {
            cancellation.ThrowIfCancellationRequested();
            string root = Path.Combine(paths.Cache, rule.Directory);
            if (!Directory.Exists(root)) continue;
            // The metadata scanner intentionally keeps only the largest 100 files. Preview walks
            // the anchored tree separately and is bounded; an incomplete category is never selectable.
            var items = new List<PlanItem>();
            var stack = new Stack<(string Path, int Depth)>();
            var policy = new LinuxCleanupPolicy([root], paths.Data, exclusions);
            RelativeGlob[] include = (rule.Include ?? ["**"]).Select(p => new RelativeGlob(p)).ToArray();
            long errors = 0, skipped = 0, visited = 0;
            bool complete = true;
            stack.Push((root, 0));
            DateTime cutoff = time.GetUtcNow().UtcDateTime.AddDays(-rule.MinimumAgeDays);
            while (stack.TryPop(out var next))
            {
                cancellation.ThrowIfCancellationRequested();
                if (exclusions.Any(p => LinuxPaths.Under(next.Path, LinuxPaths.Normalize(p)))) { skipped++; continue; }
                try
                {
                    using var dir = LinuxNative.OpenDirectory(next.Path);
                    foreach (string entry in Directory.EnumerateFileSystemEntries("/proc/self/fd/" + LinuxNative.Fd(dir)))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (++visited > 100_000) { complete = false; stack.Clear(); break; }
                        string name = Path.GetFileName(entry), path = Path.Combine(next.Path, name);
                        LinuxNative.Stat stat = LinuxNative.Read(LinuxNative.Fd(dir), name);
                        if (stat.Type == LinuxNative.DirectoryType)
                        {
                            if (next.Depth < 64) stack.Push((path, next.Depth + 1));
                            else complete = false;
                        }
                        else if (stat.Type == LinuxNative.Regular && stat.Links == 1 && stat.Mtime <= cutoff && policy.Check(path).Allowed
                            && include.Any(g => g.Matches(Path.GetRelativePath(root, path))))
                            items.Add(new(rule.Id, 1, "delete", path, checked((long)stat.Size), stat.Mtime, stat.Identity));
                        else skipped++;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors++; complete = false; }
            }
            result.Add(new(rule, items, complete, skipped, errors));
        }
        return result;
    }
}
