using Spacewise.Core;

namespace Spacewise.Catalog;

// Turns a preview into a plan without touching the file system (FR-CLN-013: same selection, no mutation).
public static class PlanBuilder
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    // Only per-file "delete" rules that need no elevation; handler rules have no file list to bind.
    public static CleanupPlan Build(IEnumerable<RulePreview> previews, DateTime createdUtc, string origin = "user", TimeSpan? lifetime = null) =>
        CleanupPlan.Create(
            previews.Where(p => p.Rule is { Action: "delete", RequiresAdmin: false })
                .SelectMany(p => p.Items.Select(i => new PlanItem(p.Rule.Id, p.Rule.RuleVersion, p.Rule.Action, i.Path, i.LogicalBytes, i.LastWriteUtc))),
            createdUtc, lifetime ?? DefaultLifetime, origin);
}
