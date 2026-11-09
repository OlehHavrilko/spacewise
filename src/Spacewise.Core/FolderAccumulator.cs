namespace Spacewise.Core;

// Bounded metadata only: neither a file list nor an additional filesystem walk.
public sealed class FolderAccumulator(string root, int maximumFolders = 4096, int maximumDepth = 3)
{
    private readonly Dictionary<string, (long Files, long Bytes)> totals = new(StringComparer.OrdinalIgnoreCase);
    public bool Truncated { get; private set; }

    public void Add(string file, long bytes)
    {
        string relative = Path.GetRelativePath(root, file);
        string current = root;
        int start = 0;
        for (int depth = 0; depth < maximumDepth; depth++)
        {
            int end = relative.IndexOf(Path.DirectorySeparatorChar, start);
            if (end < 0) break;
            current = Path.Combine(current, relative[start..end]);
            if (!totals.TryGetValue(current, out var value) && totals.Count >= maximumFolders)
            {
                Truncated = true;
                break;
            }
            totals[current] = (value.Files + 1, checked(value.Bytes + bytes));
            start = end + 1;
        }
    }

    public IReadOnlyList<FolderAggregate> Snapshot() => totals
        .Select(p => new FolderAggregate(p.Key, p.Value.Files, p.Value.Bytes))
        .OrderByDescending(p => p.LogicalBytes).ToArray();
}
