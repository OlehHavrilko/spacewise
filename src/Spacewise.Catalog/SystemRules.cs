namespace Spacewise.Catalog;

// Categories the shell cleans itself. They are not in the file catalog because they have no file list to bind into a plan.
public static class SystemRules
{
    public const string RecycleBinAction = "shell:recycle-bin";

    public static readonly CleanupRule RecycleBin = new(
        "windows.recyclebin", 1, "Корзина",
        new RuleDetect([]), [], RecycleBinAction,
        RiskClass.Caution, Consequence.Lost, Reversible: false, RequiresAdmin: false, AutomationEligible: false,
        "Файлы из Корзины удаляются окончательно, вернуть их из Корзины будет нельзя. Карантин для них не применяется.",
        new RuleVerifiedOn(["10.0.26300"]), ["recycle-bin"]);

    public static RulePreview RecycleBinPreview(long items, long bytes) =>
        new(RecycleBin, [], 0, 0, 0) { SystemCount = items, SystemBytes = bytes };
}
