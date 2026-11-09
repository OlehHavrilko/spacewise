using System.Text.Json;

namespace Spacewise.Inference;

public interface IAssistantProvider
{
    string Id { get; }
    bool IsAvailable { get; }
    Task<AssistantResponse> ExplainAsync(string question, IReadOnlyList<AssistantFact> facts, CancellationToken cancellation);
}

public sealed class DisabledAssistantProvider : IAssistantProvider
{
    public string Id => "disabled";
    public bool IsAvailable => false;
    public Task<AssistantResponse> ExplainAsync(string question, IReadOnlyList<AssistantFact> facts, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new AssistantResponse("ИИ отключён. Анализ и очистка доступны без модели.", []));
    }
}

public sealed class AssistantProviderRegistry(IEnumerable<IAssistantProvider> providers)
{
    private readonly Dictionary<string, IAssistantProvider> registered = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
    private readonly DisabledAssistantProvider disabled = new();
    public IAssistantProvider Get(string id) => id == "disabled" ? disabled : registered.GetValueOrDefault(id) ?? disabled;
}

public static class ModelProfiles
{
    public static ModelDescriptor? LoadOptional(string file)
    {
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 16384) throw new InvalidDataException("Профиль модели слишком большой.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };
        ModelDescriptor model = JsonSerializer.Deserialize<ModelDescriptor>(File.ReadAllText(file), options) ?? throw new InvalidDataException("Пустой профиль модели.");
        Validate(model);
        return model;
    }
    public static void Validate(ModelDescriptor model)
    {
        if (model.Id.Length is < 1 or > 100 || model.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.' and not '_')
            || model.FileName != Path.GetFileName(model.FileName) || model.FileName.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || !model.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            || model.SizeBytes is <= 0 or > 32L * 1024 * 1024 * 1024 || model.Sha256.Length != 64 || model.Sha256.Any(c => !Uri.IsHexDigit(c))
            || !model.DownloadUri.IsAbsoluteUri || model.DownloadUri.Scheme != "https" || model.DownloadUri.UserInfo.Length > 0
            || string.IsNullOrWhiteSpace(model.License) || !model.LicenseUri.IsAbsoluteUri || model.LicenseUri.Scheme != "https")
            throw new InvalidDataException("Недопустимый профиль модели.");
    }
}
