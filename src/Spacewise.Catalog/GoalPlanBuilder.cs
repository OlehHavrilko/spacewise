using Spacewise.Core;

namespace Spacewise.Catalog;

public sealed record GoalPlanResult(long GoalBytes, IReadOnlyList<RulePreview> Categories, long EstimatedBytes)
{
    public long UnmetBytes => Math.Max(0, GoalBytes - EstimatedBytes);
}

public static class GoalPlanBuilder
{
    public static GoalPlanResult Build(IEnumerable<RulePreview> previews, long goalBytes)
    {
        if (goalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(goalBytes));

        RulePreview[] candidates = [.. previews
            .Where(p => p.Items.Count > 0
                && p.LogicalBytes > 0
                && p.Rule.Action == "delete"
                && !p.Rule.RequiresAdmin
                && p.Rule.RiskClass == RiskClass.Safe
                && p.Rule.Consequence is Consequence.Regenerated or Consequence.Redownloaded
                && p.Running.Count == 0
                && p.LockedBy.Count == 0
                && p.LockChecked >= p.Items.Count)
            .OrderBy(p => p.Rule.Consequence == Consequence.Regenerated ? 0 : 1)
            .ThenByDescending(p => p.LogicalBytes)
            .ThenBy(p => p.Rule.Id, StringComparer.Ordinal)];

        var selected = new List<RulePreview>();
        long estimated = 0;
        foreach (RulePreview candidate in candidates)
        {
            selected.Add(candidate);
            estimated = checked(estimated + candidate.LogicalBytes);
            if (estimated >= goalBytes) break;
        }

        return new(goalBytes, selected, estimated);
    }
}
