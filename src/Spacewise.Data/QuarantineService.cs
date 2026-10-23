using System.Text.Json;
using Spacewise.Core;

namespace Spacewise.Data;

public sealed record PurgeReport(int Purged, int Failed, long LogicalBytes, long? MeasuredFreeDelta);

public sealed record RestoreReport(int Restored, int Failed);

// Quarantine lifecycle on top of the journal: restore, purge by expiry or on request, with measured free space.
public sealed class QuarantineService(ExecutionStore store, IQuarantinePurger purger, IFileRestorer restorer, IVolumeSpace space, TimeProvider time)
{
    public Task<IReadOnlyList<ExecutionStore.QuarantinedFile>> HeldAsync(CancellationToken cancellation = default) => store.HeldAsync(cancellation);

    public async Task<PurgeReport> PurgeExpiredAsync(CancellationToken cancellation = default)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        return await PurgeAsync([.. (await store.HeldAsync(cancellation)).Where(f => f.ExpiresUtc <= now)], cancellation);
    }

    public async Task<PurgeReport> PurgeAllAsync(CancellationToken cancellation = default) =>
        await PurgeAsync(await store.HeldAsync(cancellation), cancellation);

    // A file already gone counts as purged (nothing left to free). Any other failure keeps the row so it is retried.
    private async Task<PurgeReport> PurgeAsync(IReadOnlyList<ExecutionStore.QuarantinedFile> files, CancellationToken cancellation)
    {
        if (files.Count == 0) return new(0, 0, 0, null);
        string[] volumes = [.. files.Select(f => Path.GetPathRoot(f.StoredPath) ?? "").Distinct(StringComparer.OrdinalIgnoreCase)];
        long? before = TotalFree(volumes);
        int purged = 0, failed = 0;
        long bytes = 0;
        foreach (ExecutionStore.QuarantinedFile file in files)
        {
            if (cancellation.IsCancellationRequested) break;
            RemoveResult result = purger.Purge(file.StoredPath);
            if (result.Status is RemoveStatus.Removed or RemoveStatus.Gone)
            {
                await store.MarkPurgedAsync(file.Id, time.GetUtcNow().UtcDateTime, CancellationToken.None);
                purged++;
                if (result.Status == RemoveStatus.Removed) bytes += file.Size;
            }
            else failed++;
        }
        long? after = TotalFree(volumes);
        long? delta = before is long b && after is long a ? a - b : null;
        if (purged > 0)
            await store.SaveSettingAsync("last_purge", JsonSerializer.Serialize(new
            {
                utc = time.GetUtcNow().UtcDateTime, purged, failed, logical_bytes = bytes, free_before = before, free_after = after,
            }), CancellationToken.None);
        return new(purged, failed, bytes, delta);
    }

    private long? TotalFree(IEnumerable<string> volumes)
    {
        long total = 0;
        foreach (string volume in volumes)
        {
            if (space.FreeBytes(volume) is not long free) return null;
            total += free;
        }
        return total;
    }

    public async Task<RestoreReport> RestoreAllAsync(CancellationToken cancellation = default)
    {
        int restored = 0, failed = 0;
        foreach (ExecutionStore.QuarantinedFile file in await store.HeldAsync(cancellation))
        {
            if (restorer.Restore(file.StoredPath, file.OriginalPath).Restored)
            {
                await store.MarkRestoredAsync(file.Id, time.GetUtcNow().UtcDateTime, CancellationToken.None);
                restored++;
            }
            else failed++;
        }
        return new(restored, failed);
    }
}
