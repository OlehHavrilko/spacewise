using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Spacewise.Inference;

public sealed record AssistantFact(string Id, string Category, string Consequence, string SideEffects, long FileCount, long LogicalBytes);
public sealed record AssistantSuggestion(string FactId, string ReasonCode, string ExplanationRu);
public sealed record AssistantResponse(string SummaryRu, IReadOnlyList<AssistantSuggestion> Suggestions);
public sealed record InferenceRequest(string ModelPath, string Question, IReadOnlyList<AssistantFact> Facts, ModelDescriptor? Model = null);

public static partial class AssistantResponseValidator
{
    private sealed record Selection(string Topic, IReadOnlyList<string> FactIds);
    private static readonly HashSet<string> Reasons = ["regenerated", "redownloaded", "permanent", "unknown"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 8,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static void ValidateFacts(IReadOnlyList<AssistantFact> facts)
    {
        if (facts.Count is 0 or > 32) throw new InvalidDataException("Invalid fact count.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (AssistantFact fact in facts)
        {
            if (!FactIdPattern().IsMatch(fact.Id) || !ids.Add(fact.Id)
                || !ValidText(fact.Category, 120) || !ValidText(fact.SideEffects, 240)
                || !Reasons.Contains(fact.Consequence) || fact.FileCount < 0 || fact.LogicalBytes < 0)
                throw new InvalidDataException("Invalid or path-bearing fact.");
        }
    }

    public static string RedactPaths(string text) => PathPattern().Replace(text, "[path hidden]");

    // Small models select facts; all visible claims are derived from trusted catalog fields.
    public static AssistantResponse FromSelection(string json, IReadOnlyList<AssistantFact> facts, string? question = null)
    {
        ValidateFacts(facts);
        if (json.Length > 2048) throw new InvalidDataException("Выбор модели длиннее допустимого.");
        Selection selection;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateKeys(document.RootElement);
            selection = JsonSerializer.Deserialize<Selection>(json, JsonOptions)
                ?? throw new InvalidDataException("Модель не выбрала факты.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Выбор модели обрезан или имеет неверный формат.", ex); }
        string topic = selection.Topic;
        if (question is not null)
        {
            if (MutationQuestion().IsMatch(question)) topic = "limits";
            else if (SpaceQuestion().IsMatch(question)) topic = "space";
        }
        string summary = topic switch
        {
            "space" => "Размер категорий — логическая оценка. Реальное изменение свободного места измеряется на томе после очистки и может отличаться.",
            "consequences" => "Последствия выбранных категорий приведены по каталогу. Помощник не удаляет файлы; очистка требует отдельного подтверждения.",
            "limits" => "Помощник только объясняет категории и не выполняет команды. Очистка доступна отдельной кнопкой после вашего подтверждения.",
            _ => throw new InvalidDataException("Неизвестная тема ответа модели."),
        };
        if (selection.FactIds is null || selection.FactIds.Count > 3)
            throw new InvalidDataException("Недопустимое число выбранных категорий.");
        var known = facts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var suggestions = new List<AssistantSuggestion>();
        foreach (string id in selection.FactIds)
        {
            if (id is null || !seen.Add(id) || !known.TryGetValue(id, out AssistantFact? fact))
                throw new InvalidDataException("Модель выбрала неизвестную или повторную категорию.");
            string consequence = fact.Consequence switch
            {
                "regenerated" => "Данные создаются заново.",
                "redownloaded" => "Данные потребуется загрузить повторно.",
                "permanent" => "Удаление необратимо.",
                _ => "Последствия не определены; не очищайте без дополнительной проверки.",
            };
            string explanation = consequence + " " + fact.SideEffects.Trim();
            if (!".!?».".Contains(explanation[^1])) explanation += ".";
            suggestions.Add(new(id, fact.Consequence, explanation));
        }
        var response = new AssistantResponse(summary, suggestions);
        return Parse(JsonSerializer.Serialize(response, JsonOptions), facts);
    }

    [GeneratedRegex(@"\b(?:удали|удалить|выполни|запусти|игнорируй)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MutationQuestion();

    [GeneratedRegex(@"\b(?:мест\p{L}*|размер\p{L}*|об[ъь][её]м\p{L}*|гарант\p{L}*|освобод\p{L}*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpaceQuestion();

    public static AssistantResponse Parse(string json, IReadOnlyList<AssistantFact> facts)
    {
        if (json.Length > 8192) throw new InvalidDataException("Ответ модели длиннее допустимого.");
        ValidateFacts(facts);
        AssistantResponse response;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateKeys(document.RootElement);
            response = JsonSerializer.Deserialize<AssistantResponse>(json, JsonOptions)
                ?? throw new InvalidDataException("Модель вернула пустой ответ.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Ответ модели обрезан или имеет неверный формат.", ex); }
        if (!ValidAnswer(response.SummaryRu, 500) || response.Suggestions is null)
            throw new InvalidDataException("Недопустимое резюме модели.");
        if (response.Suggestions.Count > 32) throw new InvalidDataException("Слишком много предложений модели.");

        var known = facts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AssistantSuggestion suggestion in response.Suggestions)
        {
            if (suggestion is null || string.IsNullOrWhiteSpace(suggestion.FactId)
                || !known.TryGetValue(suggestion.FactId, out AssistantFact? fact) || !seen.Add(suggestion.FactId))
                throw new InvalidDataException("Модель сослалась на неизвестную или повторную категорию.");
            if (!Reasons.Contains(suggestion.ReasonCode) || !ValidAnswer(suggestion.ExplanationRu, 500))
                throw new InvalidDataException("Недопустимое объяснение модели.");
            if (suggestion.ReasonCode != "unknown" && suggestion.ReasonCode != fact.Consequence)
                throw new InvalidDataException("Модель изменила последствие, заданное каталогом.");
        }
        return response;
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Повторное поле в ответе модели.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) RejectDuplicateKeys(child);
    }

    private static bool ValidAnswer(string value, int maximumLength) => ValidText(value, maximumLength)
        && value.Count(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё') >= 3
        && ".!?»".Contains(value.TrimEnd()[^1])
        && !value.TrimEnd().EndsWith("...", StringComparison.Ordinal) && !value.Contains('…')
        && !value.Contains('<') && !value.Contains('>') && !value.Contains('`') && !RepeatedWords().IsMatch(value)
        && !value.Any(c => c is >= '\u3400' and <= '\u9fff' or '\ufffd');

    [GeneratedRegex(@"\b(\p{L}+)\b(?:[\s,;]+\1\b){2,}", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatedWords();

    private static bool ValidText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength
        && !value.Any(char.IsControl) && !PathPattern().IsMatch(value);

    [GeneratedRegex(@"(?i)(?:[a-z]:[\\/][^\r\n,;]*|\\\\[^\\\r\n,;]+(?:\\[^\\\r\n,;]+)*)")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^F[0-9]{3}$")]
    private static partial Regex FactIdPattern();
}
