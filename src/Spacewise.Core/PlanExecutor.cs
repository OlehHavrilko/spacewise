namespace Spacewise.Core;

public enum RemoveStatus { Removed, Gone, Changed, Locked, Unsafe, Failed }

public sealed record RemoveResult(RemoveStatus Status, int Code = 0, string? StoredPath = null);

// Platform side of deletion: must act on an object opened relative to a verified parent (spec §7.4), never on a bare path.
public interface IFileRemover
{
    RemoveResult Remove(PlanItem item);
}

public sealed record RestoreResult(bool Restored, int Code = 0);

public interface IFileRestorer
{
    // Puts a quarantined file back under its original name; never replaces an existing file.
    RestoreResult Restore(string storedPath, string originalPath);
}

public sealed record ItemOutcome(PlanItem Item, RemoveStatus Status, int Code = 0, string? StoredPath = null);

public sealed record ExecutionReport(IReadOnlyList<ItemOutcome> Outcomes, bool Cancelled)
{
    public int Removed => Outcomes.Count(o => o.Status == RemoveStatus.Removed);
    public long BytesRemoved => Outcomes.Where(o => o.Status == RemoveStatus.Removed).Sum(o => o.Item.ExpectedSize);
}

public sealed class PlanRejectedException(string message) : Exception(message);

// Runs a plan: re-verifies digest, expiry and the protected-path policy, then removes item by item.
// A changed, locked or vanished file is skipped and reported; one item never stops the rest.
public sealed class PlanExecutor(IFileRemover remover, UserFolders folders, TimeProvider time)
{
    public ExecutionReport Execute(CleanupPlan plan, CancellationToken cancellation = default)
    {
        PolicyDecision decision = MutationPolicy.Evaluate(plan, time.GetUtcNow().UtcDateTime);
        if (!decision.Allowed) throw new PlanRejectedException(decision.Reason);

        var outcomes = new List<ItemOutcome>(plan.Items.Count);
        foreach (PlanItem item in plan.Items)
        {
            if (cancellation.IsCancellationRequested) return new(outcomes, Cancelled: true);
            if (item.ActionId != "delete" || !ProtectedPaths.Check(item.Path, folders).Allowed)
            {
                outcomes.Add(new(item, RemoveStatus.Unsafe));
                continue;
            }
            RemoveResult result;
            try { result = remover.Remove(item); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                result = new(RemoveStatus.Failed, e.HResult);
            }
            outcomes.Add(new(item, result.Status, result.Code, result.StoredPath));
        }
        return new(outcomes, Cancelled: false);
    }
}
