namespace Spacewise.Catalog;

// FR-AI-001: explanations come from catalog facts only, with no model. Every sentence maps to a rule field.
public static class RuleExplainer
{
    public static string Explain(RulePreview preview)
    {
        CleanupRule rule = preview.Rule;
        var parts = new List<string>
        {
            $"{rule.TitleRu}: найдено файлов {preview.FileCount}, оценка по логическим размерам {Bytes(preview.LogicalBytes)}.",
            rule.Consequence switch
            {
                Consequence.Regenerated => "Это кэш: приложение создаст его заново.",
                Consequence.Redownloaded => "Данные будут скачаны заново при следующем использовании.",
                _ when rule.Action == SystemRules.RecycleBinAction => "Это файлы, которые вы уже удалили: после очистки Корзины они пропадут.",
                _ => "Данные не восстанавливаются приложением.",
            },
            rule.SideEffectsRu,
        };
        if (rule.Action == SystemRules.RecycleBinAction) parts.Add("Корзина очищается средствами Windows.");
        else if (rule.RequiresAdmin) parts.Add("Для этой категории нужны права администратора, в этой версии выполняется только оценка.");
        else if (rule.Action != "delete") parts.Add("Категория очищается системным обработчиком Windows, в этой версии выполняется только оценка.");
        if (rule.Action == SystemRules.RecycleBinAction || rule is { Action: "delete", RequiresAdmin: false })
            parts.Add("Действие необратимо: файлы удаляются без карантина и Корзины.");
        parts.Add($"Основание: правило {rule.Id}, версия {rule.RuleVersion}.");
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    // Local to this class so Catalog does not depend on Core's formatting for a one-line fact.
    private static string Bytes(long value) => Spacewise.Core.DisplayFormat.Size(value);
}
