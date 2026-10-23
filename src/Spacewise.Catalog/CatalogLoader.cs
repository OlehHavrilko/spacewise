using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spacewise.Catalog;

public sealed class CatalogException(string message) : Exception(message);

// Loads and validates the catalog. The built-in catalog is an embedded resource of this assembly;
// signature checking (Ed25519, spec §4.9) applies to downloaded updates and is not implemented yet.
public static partial class CatalogLoader
{
    public const int MaximumBytes = 1 << 20;
    public static readonly IReadOnlySet<string> Variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "USERPROFILE", "LOCALAPPDATA", "APPDATA", "TEMP", "SYSTEMROOT" };
    // Actions the executor will know; "handler:<name>" refers to a Disk Cleanup handler (ADR 0003).
    private static readonly string[] Actions = ["delete"];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    public static CleanupCatalog LoadBuiltIn()
    {
        using Stream stream = typeof(CatalogLoader).Assembly.GetManifestResourceStream("Spacewise.Catalog.catalog.json")
            ?? throw new CatalogException("Встроенный каталог не найден.");
        return Load(stream);
    }

    // minimumVersion: the newest version already accepted; an older catalog is a downgrade.
    public static CleanupCatalog Load(Stream stream, int minimumVersion = 0)
    {
        var buffer = new byte[MaximumBytes + 1];
        int length = 0, read;
        while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0) length += read;
        if (length > MaximumBytes) throw new CatalogException($"Каталог больше {MaximumBytes} байт.");

        CleanupCatalog catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<CleanupCatalog>(buffer.AsSpan(0, length), Options)
                ?? throw new CatalogException("Пустой каталог.");
        }
        catch (JsonException e) { throw new CatalogException($"Каталог не разобран: {e.Message}"); }

        if (catalog.Version < minimumVersion)
            throw new CatalogException($"Версия каталога {catalog.Version} ниже принятой {minimumVersion}.");
        Validate(catalog);
        return catalog;
    }

    private static void Validate(CleanupCatalog catalog)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (CleanupRule rule in catalog.Rules)
        {
            void Require(bool ok, string what) { if (!ok) throw new CatalogException($"{rule.Id}: {what}."); }

            Require(RuleId().IsMatch(rule.Id ?? ""), "id из строчных латинских слов через точку");
            Require(ids.Add(rule.Id!), "id повторяется");
            Require(rule.RuleVersion > 0, "ruleVersion больше нуля");
            Require(!string.IsNullOrWhiteSpace(rule.TitleRu) && !string.IsNullOrWhiteSpace(rule.SideEffectsRu), "title_ru и sideEffects_ru заполнены");
            Require(Actions.Contains(rule.Action) || rule.Action.StartsWith("handler:", StringComparison.Ordinal), $"неизвестное действие {rule.Action}");
            Require(rule.Fixtures is { Count: > 0 }, "нужна хотя бы одна fixture (FR-CLN-012)");
            Require(rule.Detect.AnyPath is { Count: > 0 } && rule.Detect.AnyPath.All(IsRootTemplate), "detect.anyPath из шаблонов %VAR%\\...");
            Require(rule.Targets is { Count: > 0 }, "нужна хотя бы одна цель");
            bool usesProfile = false;
            foreach (RuleTarget target in rule.Targets)
            {
                bool profileRoot = target.Root == "{profile}"
                    || target.Root.StartsWith(@"{profile}\", StringComparison.Ordinal) && IsRelativePattern(target.Root[@"{profile}\".Length..]);
                usesProfile |= profileRoot;
                Require(IsRootTemplate(target.Root) || profileRoot, $"корень {target.Root}");
                Require(target.Include is { Count: > 0 } && target.Include.All(IsRelativePattern), "include — относительные шаблоны без .., : и /");
                Require(target.MinAgeDays >= 0, "minAgeDays не отрицательный");
            }
            Require(usesProfile == rule.Profiles is not null, "{profile} в целях только вместе с profiles");
            if (rule.Profiles is { } p)
                Require(IsRootTemplate(p.Root) && p.Include is { Count: > 0 } && p.Include.All(n => IsRelativePattern(n) && !n.Contains('\\')),
                    "profiles: корень %VAR%\\... и шаблоны имён папок");
            Require(rule.Consequence != Consequence.Lost || rule.RiskClass != RiskClass.Safe, "потеря данных не может быть safe");
        }
    }

    // %VAR% or %VAR%\relative, with VAR from the known set.
    private static bool IsRootTemplate(string root)
    {
        Match m = RootTemplate().Match(root);
        return m.Success && Variables.Contains(m.Groups[1].Value) && (m.Groups[2].Value.Length == 0 || IsRelativePattern(m.Groups[2].Value[1..]));
    }

    private static bool IsRelativePattern(string pattern) =>
        pattern.Length > 0 && !pattern.StartsWith('\\') && !pattern.EndsWith('\\') && pattern.IndexOfAny([':', '/', '%', '{']) < 0
        && pattern.Split('\\').All(s => s.Length > 0 && s is not "." and not "..");

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9-]+)+$")]
    private static partial Regex RuleId();

    [GeneratedRegex(@"^%([A-Za-z]+)%(\\.*)?$")]
    private static partial Regex RootTemplate();
}
