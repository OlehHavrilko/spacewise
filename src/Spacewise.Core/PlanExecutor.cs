namespace Spacewise.Core;

public enum RemoveStatus { Removed, Gone, Changed, Locked, Unsafe, Failed, Interrupted }

public sealed record RemoveResult(RemoveStatus Status, int Code = 0, string? StoredPath = null, byte[]? Sha256 = null);

// Platform side of deletion: must act on an object opened relative to a verified parent (spec §7.4), never on a bare path.
public interface IFileRemover
{
    RemoveResult Remove(PlanItem item);
}

// Permanent deletion of a file that already sits in quarantine; only ever applied to quarantine contents.
public interface IQuarantinePurger
{
    RemoveResult Purge(string storedPath);
}

// Free bytes on the volume holding a path; null when unknown. Used to report measured, not estimated, gains.
public interface IVolumeSpace
{
    long? FreeBytes(string path);
}

public sealed record RestoreResult(bool Restored, int Code = 0, bool HashMismatch = false);

public interface IFileRestorer
{
    // Puts a quarantined file back under its original name; never replaces an existing file.
    // With expectedSha256 the stored content is verified first and a mismatch leaves the file in quarantine.
    RestoreResult Restore(string storedPath, string originalPath, byte[]? expectedSha256 = null);
}

public sealed record ItemOutcome(PlanItem Item, RemoveStatus Status, int Code = 0, string? StoredPath = null, byte[]? Sha256 = null);

public sealed record ExecutionReport(IReadOnlyList<ItemOutcome> Outcomes, bool Cancelled)
{
    public int Removed => Outcomes.Count(o => o.Status == RemoveStatus.Removed);
    public long BytesRemoved => Outcomes.Where(o => o.Status == RemoveStatus.Removed).Sum(o => o.Item.ExpectedSize);
}

// Write-ahead hook: Begin is recorded before an item is touched, Complete right after, so a crash leaves at most one item in doubt.
public interface IExecutionJournal
{
    void Begin(int seq, PlanItem item);
    void Complete(int seq, ItemOutcome outcome);
}

public sealed class PlanRejectedException(string message) : Exception(message);

// Runs a plan: re-verifies digest, expiry and the protected-path policy, then removes item by item.
// A changed, locked or vanished file is skipped and reported; one item never stops the rest.
public sealed class PlanExecutor(IFileRemover remover, UserFolders folders, TimeProvider time)
{
    public ExecutionReport Execute(CleanupPlan plan, CancellationToken cancellation = default, IExecutionJournal? journal = null)
    {
        PolicyDecision decision = MutationPolicy.Evaluate(plan, time.GetUtcNow().UtcDateTime);
        if (!decision.Allowed) throw new PlanRejectedException(decision.Reason);

        var outcomes = new List<ItemOutcome>(plan.Items.Count);
        for (int seq = 0; seq < plan.Items.Count; seq++)
        {
            PlanItem item = plan.Items[seq];
            if (cancellation.IsCancellationRequested) return new(outcomes, Cancelled: true);
            if (item.ActionId != "delete" || !ProtectedPaths.Check(item.Path, folders).Allowed)
            {
                var refused = new ItemOutcome(item, RemoveStatus.Unsafe);
                outcomes.Add(refused);
                journal?.Complete(seq, refused);
                continue;
            }
            journal?.Begin(seq, item);
            RemoveResult result;
            try { result = remover.Remove(item); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                result = new(RemoveStatus.Failed, e.HResult);
            }
            var outcome = new ItemOutcome(item, result.Status, result.Code, result.StoredPath, result.Sha256);
            outcomes.Add(outcome);
            journal?.Complete(seq, outcome);
        }
        return new(outcomes, Cancelled: false);
    }
}
