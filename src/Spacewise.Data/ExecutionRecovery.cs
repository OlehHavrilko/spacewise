using Spacewise.Core;

namespace Spacewise.Data;

public sealed record RecoveryReport(int NeverMoved, int Recovered, int Unknown, int UnrecordedFiles);

// A5: after a crash every item of an interrupted plan ends in a known state, and nothing is retried blindly.
// An "in_progress" item is settled by looking at the disk: still in place (never moved), found in quarantine by size and
// mtime (moved, so it is recorded and becomes restorable), or neither (reported as Interrupted).
public sealed class ExecutionRecovery(ExecutionStore store, PlanStore plans, string quarantineDirectory, TimeProvider time)
{
    public async Task<RecoveryReport> RecoverAsync(CancellationToken cancellation = default)
    {
        var interrupted = await store.InterruptedAsync(cancellation);
        int neverMoved = 0, recovered = 0, unknown = 0;
        HashSet<string> known = new(await store.KnownStoredPathsAsync(cancellation), StringComparer.OrdinalIgnoreCase);
        List<FileInfo> orphans = Directory.Exists(quarantineDirectory)
            ? [.. new DirectoryInfo(quarantineDirectory).EnumerateFiles().Where(f => !known.Contains(f.FullName))]
            : [];
        DateTime now = time.GetUtcNow().UtcDateTime;

        foreach (ExecutionStore.InterruptedItem entry in interrupted)
        {
            CleanupPlan? plan = await plans.LoadAsync(entry.PlanId, cancellation);
            if (plan is null || entry.Seq >= plan.Items.Count) continue;
            PlanItem item = plan.Items[entry.Seq];
            var original = new FileInfo(item.Path);
            if (original.Exists && original.Length == item.ExpectedSize)
            {
                await store.CompleteItemAsync(entry.PlanId, entry.Seq, new(item, RemoveStatus.Interrupted), now, cancellation: cancellation);
                neverMoved++;
                continue;
            }
            FileInfo? moved = orphans.FirstOrDefault(f => f.Length == item.ExpectedSize && f.LastWriteTimeUtc == item.ExpectedMtimeUtc);
            if (moved is not null)
            {
                orphans.Remove(moved);
                await store.CompleteItemAsync(entry.PlanId, entry.Seq, new(item, RemoveStatus.Removed, 0, moved.FullName), now, cancellation: cancellation);
                recovered++;
            }
            else
            {
                await store.CompleteItemAsync(entry.PlanId, entry.Seq, new(item, RemoveStatus.Interrupted, 1), now, cancellation: cancellation);
                unknown++;
            }
        }
        await store.CloseUnfinishedPlansAsync(cancellation);
        return new(neverMoved, recovered, unknown, orphans.Count);
    }
}
