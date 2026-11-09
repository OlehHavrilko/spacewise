namespace Spacewise.Core;

public sealed record DuplicateCandidate(string Path, long Size, DateTime MtimeUtc, FileIdentity Identity);
public sealed record DuplicateGroup(string Blake3, IReadOnlyList<DuplicateCandidate> Files)
{
    public long PotentialBytes => Files.Count > 1 ? checked((Files.Count - 1) * Files[0].Size) : 0;
}
public sealed record DuplicateReport(IReadOnlyList<DuplicateGroup> Groups, long Candidates, long BytesRead, long Skipped, bool Limited);

public sealed class DuplicateRead(Stream stream, Func<bool> unchanged) : IDisposable
{
    public Stream Stream => stream;
    public bool IsUnchanged() => unchanged();
    public void Dispose() => stream.Dispose();
}

public interface IDuplicateSource
{
    IEnumerable<DuplicateCandidate> Candidates(string root, IReadOnlyList<string> exclusions, CancellationToken cancellation);
    DuplicateRead Open(DuplicateCandidate candidate);
}

public sealed class DuplicateFinder(IDuplicateSource source)
{
    public Task<DuplicateReport> FindAsync(string root, IReadOnlyList<string> exclusions, CancellationToken cancellation,
        int maximumCandidates = 100_000, long maximumHashBytes = 16L * 1024 * 1024 * 1024) => Task.Run(async () =>
    {
        if (maximumCandidates < 1 || maximumHashBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        var candidates = new List<DuplicateCandidate>();
        var identities = new HashSet<FileIdentity>();
        bool limited = false;
        long skipped = 0, bytesRead = 0;
        foreach (DuplicateCandidate file in source.Candidates(root, exclusions, cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            if (candidates.Count >= maximumCandidates) { limited = true; break; }
            if (file.Size <= 0 || !identities.Add(file.Identity)) { skipped++; continue; }
            candidates.Add(file);
        }
        var groups = new List<DuplicateGroup>();
        byte[] buffer = new byte[128 * 1024];
        foreach (var sameSize in candidates.GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            var hashes = new Dictionary<string, List<DuplicateCandidate>>(StringComparer.Ordinal);
            foreach (DuplicateCandidate file in sameSize)
            {
                cancellation.ThrowIfCancellationRequested();
                if (file.Size > maximumHashBytes - bytesRead) { limited = true; skipped++; continue; }
                try
                {
                    using DuplicateRead input = source.Open(file);
                    using var hasher = Blake3.Hasher.New();
                    long read = 0;
                    int count;
                    while ((count = await input.Stream.ReadAsync(buffer, cancellation)) != 0)
                    {
                        read += count; bytesRead += count;
                        if (read > file.Size || bytesRead > maximumHashBytes) { limited = true; break; }
                        hasher.Update(buffer.AsSpan(0, count));
                    }
                    if (read != file.Size || !input.IsUnchanged() || bytesRead > maximumHashBytes) { skipped++; continue; }
                    string hash = hasher.Finalize().ToString();
                    if (!hashes.TryGetValue(hash, out var matching)) hashes[hash] = matching = [];
                    matching.Add(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
            }
            groups.AddRange(hashes.Where(p => p.Value.Count > 1).Select(p => new DuplicateGroup(p.Key, p.Value)));
        }
        return new DuplicateReport(groups.OrderByDescending(g => g.PotentialBytes).ToArray(), candidates.Count, bytesRead, skipped, limited);
    }, cancellation);
}
