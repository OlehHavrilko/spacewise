using Spacewise.Catalog;

namespace Spacewise.Tests.Unit;

public sealed class GoalPlanTests
{
    private static RulePreview Preview(string id, long bytes, RiskClass risk = RiskClass.Safe,
        Consequence consequence = Consequence.Regenerated, string action = "delete", bool requiresAdmin = false)
    {
        var rule = new CleanupRule(id, 1, id, new RuleDetect([@"C:\Temp"]), [new RuleTarget(@"C:\Temp", ["**"])],
            action, risk, consequence, Reversible: false, RequiresAdmin: requiresAdmin, AutomationEligible: false,
            SideEffectsRu: "Проверка", VerifiedOn: new RuleVerifiedOn(["10"]), Fixtures: []);
        return new RulePreview(rule, [new PreviewItem($@"C:\Temp\{id}.tmp", bytes, DateTime.UtcNow)], 0, 0, 0)
        {
            LockChecked = 1,
        };
    }

    [Fact]
    public void Goal_plan_uses_only_safe_rebuildable_or_redownloadable_file_categories()
    {
        RulePreview regenerated = Preview("cache.regenerated", 6);
        RulePreview redownloaded = Preview("cache.redownloaded", 10, consequence: Consequence.Redownloaded);
        RulePreview caution = Preview("data.caution", 100, risk: RiskClass.Caution);
        RulePreview admin = Preview("system.admin", 100, requiresAdmin: true);
        RulePreview lost = Preview("data.lost", 100, risk: RiskClass.Expert, consequence: Consequence.Lost);
        RulePreview handler = Preview("system.handler", 100, action: "handler:clean");

        GoalPlanResult result = GoalPlanBuilder.Build(
            [lost, handler, caution, admin, redownloaded, regenerated], 15);

        Assert.Equal(15, result.GoalBytes);
        Assert.Equal(16, result.EstimatedBytes);
        Assert.Equal(0, result.UnmetBytes);
        Assert.Equal(["cache.regenerated", "cache.redownloaded"], result.Categories.Select(p => p.Rule.Id));
    }

    [Fact]
    public void Goal_plan_reports_shortfall_without_falling_back_to_caution_or_lost_data()
    {
        RulePreview safe = Preview("cache.safe", 7);
        RulePreview lost = Preview("data.lost", 100, risk: RiskClass.Caution, consequence: Consequence.Lost);

        GoalPlanResult result = GoalPlanBuilder.Build([safe, lost], 20);

        Assert.Equal([safe], result.Categories);
        Assert.Equal(7, result.EstimatedBytes);
        Assert.Equal(13, result.UnmetBytes);
    }

    [Fact]
    public void Goal_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GoalPlanBuilder.Build([], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GoalPlanBuilder.Build([], -1));
    }

    [Fact]
    public void Goal_plan_excludes_running_locked_or_partially_checked_categories()
    {
        RulePreview running = Preview("cache.running", 7) with { Running = ["browser.exe"] };
        RulePreview locked = Preview("cache.locked", 8) with { LockedBy = [new(42, "Browser")] };
        RulePreview partial = Preview("cache.partial", 9) with { LockChecked = 0 };

        GoalPlanResult result = GoalPlanBuilder.Build([running, locked, partial], 5);

        Assert.Empty(result.Categories);
        Assert.Equal(5, result.UnmetBytes);
    }
}
