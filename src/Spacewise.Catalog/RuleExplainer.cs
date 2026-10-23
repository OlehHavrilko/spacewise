namespace Spacewise.Catalog;

// FR-AI-001: explanations come from catalog facts only, with no model. Every sentence maps to a rule field.
public static class RuleExplainer
{
    public static string Explain(RulePreview preview)
    {
        CleanupRule rule = preview.Rule;
        var parts = new List<string>
        {
            $"{rule.TitleRu}: найдено файлов {preview.Items.Count}, оценка по логическим размерам {Bytes(preview.LogicalBytes)}.",
            rule.Consequence switch
            {
                Consequence.Regenerated => "Это кэш: приложение создаст его заново.",
                Consequence.Redownloaded => "Данные будут скачаны заново при следующем использовании.",
                _ => "Данные не восстанавливаются приложением, поэтому они переносятся в карантин, откуда их можно вернуть.",
            },
            rule.SideEffectsRu,
        };
        if (rule.RequiresAdmin) parts.Add("Для этой категории нужны права администратора, в этой версии выполняется только оценка.");
        else if (rule.Action != "delete") parts.Add("Категория очищается системным обработчиком Windows, в этой версии выполняется только оценка.");
        parts.Add(rule.Reversible ? "Действие обратимо." : "Файлы переносятся в карантин на 30 дней; после этого удаляются безвозвратно.");
        parts.Add($"Основание: правило {rule.Id}, версия {rule.RuleVersion}.");
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    // Local to this class so Catalog does not depend on Core's formatting for a one-line fact.
    private static string Bytes(long value) => Spacewise.Core.DisplayFormat.Size(value);
}
