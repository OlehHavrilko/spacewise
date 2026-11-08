using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spacewise.Inference;

public sealed record AssistantFact(string Id, string Category, string Consequence, string SideEffects, long FileCount, long LogicalBytes);
public sealed record AssistantSuggestion(string FactId, string ReasonCode, string ExplanationRu);
public sealed record AssistantResponse(string SummaryRu, IReadOnlyList<AssistantSuggestion> Suggestions);
public sealed record InferenceRequest(string ModelPath, string Question, IReadOnlyList<AssistantFact> Facts);

public static partial class AssistantResponseValidator
{
    private static readonly HashSet<string> Reasons = ["regenerated", "redownloaded", "permanent", "unknown"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 8,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
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

    public static AssistantResponse Parse(string json, IReadOnlyList<AssistantFact> facts)
    {
        if (json.Length > 8192) throw new InvalidDataException("Ответ модели длиннее допустимого.");
        ValidateFacts(facts);
        AssistantResponse response = JsonSerializer.Deserialize<AssistantResponse>(json, JsonOptions)
            ?? throw new InvalidDataException("Модель вернула пустой ответ.");
        if (!ValidText(response.SummaryRu, 500) || response.Suggestions is null)
            throw new InvalidDataException("Недопустимое резюме модели.");
        if (response.Suggestions.Count > 32) throw new InvalidDataException("Слишком много предложений модели.");

        var known = facts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AssistantSuggestion suggestion in response.Suggestions)
        {
            if (!known.TryGetValue(suggestion.FactId, out AssistantFact? fact) || !seen.Add(suggestion.FactId))
                throw new InvalidDataException("Модель сослалась на неизвестную или повторную категорию.");
            if (!Reasons.Contains(suggestion.ReasonCode) || !ValidText(suggestion.ExplanationRu, 240))
                throw new InvalidDataException("Недопустимое объяснение модели.");
            if (suggestion.ReasonCode != "unknown" && suggestion.ReasonCode != fact.Consequence)
                throw new InvalidDataException("Модель изменила последствие, заданное каталогом.");
        }
        return response;
    }

    private static bool ValidText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength
        && !value.Any(char.IsControl) && !PathPattern().IsMatch(value);

    [GeneratedRegex(@"(?i)(?:[a-z]:[\\/][^\r\n,;]*|\\\\[^\\\r\n,;]+(?:\\[^\\\r\n,;]+)*)")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^F[0-9]{3}$")]
    private static partial Regex FactIdPattern();
}
