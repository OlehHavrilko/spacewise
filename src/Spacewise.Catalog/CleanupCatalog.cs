using System.Text.Json.Serialization;

namespace Spacewise.Catalog;

// Spec §4.9: rules are data; imperative actions are adapters the catalog refers to by action id.
public sealed record CleanupCatalog(int Version, IReadOnlyList<CleanupRule> Rules);

[JsonConverter(typeof(JsonStringEnumConverter<RiskClass>))]
public enum RiskClass { Safe, Caution, Privacy, Expert }

[JsonConverter(typeof(JsonStringEnumConverter<Consequence>))]
public enum Consequence { Regenerated, Redownloaded, Lost }

public sealed record CleanupRule(
    string Id,
    int RuleVersion,
    [property: JsonPropertyName("title_ru")] string TitleRu,
    RuleDetect Detect,
    IReadOnlyList<RuleTarget> Targets,
    string Action,
    RiskClass RiskClass,
    Consequence Consequence,
    bool Reversible,
    bool RequiresAdmin,
    bool AutomationEligible,
    [property: JsonPropertyName("sideEffects_ru")] string SideEffectsRu,
    RuleVerifiedOn VerifiedOn,
    IReadOnlyList<string> Fixtures,
    RuleProfiles? Profiles = null,
    IReadOnlyList<string>? RunningProcess = null);

public sealed record RuleDetect(IReadOnlyList<string> AnyPath);

// Expands the {profile} root: every subdirectory of Root whose name matches one of Include.
public sealed record RuleProfiles(string Root, IReadOnlyList<string> Include);

// Include globs are relative to Root: '*' and '?' stay inside one segment, '**' spans segments.
public sealed record RuleTarget(string Root, IReadOnlyList<string> Include, int MinAgeDays = 0);

public sealed record RuleVerifiedOn(IReadOnlyList<string> Os, string? App = null);
