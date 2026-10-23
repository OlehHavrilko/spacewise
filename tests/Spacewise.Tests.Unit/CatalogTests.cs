using System.Text;
using Spacewise.Catalog;

namespace Spacewise.Tests.Unit;

public class CatalogTests
{
    [Theory]
    [InlineData("Cache\\**", "Cache\\a", true)]
    [InlineData("Cache\\**", "Cache\\x\\y\\z", true)]
    [InlineData("Cache\\**", "cache\\A", true)]
    [InlineData("Cache\\**", "Cache", false)]
    [InlineData("Cache\\**", "CacheX\\a", false)]
    [InlineData("**", "a\\b", true)]
    [InlineData("**\\*.log", "x.log", true)]
    [InlineData("**\\*.log", "a\\b\\x.log", true)]
    [InlineData("thumbcache_*.db", "thumbcache_256.db", true)]
    [InlineData("thumbcache_*.db", "sub\\thumbcache_256.db", false)]
    [InlineData("Profile *", "Profile 12", true)]
    [InlineData("Profile *", "System Profile", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "a\\c", false)]
    [InlineData("a.b", "aXb", false)]
    public void Glob_matches(string pattern, string path, bool expected) => Assert.Equal(expected, new Glob(pattern).IsMatch(path));

    [Fact]
    public void Built_in_catalog_loads_and_is_valid()
    {
        CleanupCatalog catalog = CatalogLoader.LoadBuiltIn();
        Assert.True(catalog.Version >= 1);
        // FR-CLN-010: at least 25 rules in MVP.
        Assert.True(catalog.Rules.Count >= 25);
        Assert.All(catalog.Rules, r => Assert.True(r.Action == "delete" || r.Action.StartsWith("handler:", StringComparison.Ordinal)));
    }

    // FR-CLN-012: a rule without a fixture fails the build.
    [Fact]
    public void Every_rule_fixture_exists()
    {
        IEnumerable<string> missing = CatalogLoader.LoadBuiltIn().Rules.SelectMany(r => r.Fixtures)
            .Where(f => !RuleFixtures.All.ContainsKey(f));
        Assert.Empty(missing);
    }

    private const string Rule = """
        {"id":"x.y","ruleVersion":1,"title_ru":"t","detect":{"anyPath":["%TEMP%"]},
         "targets":[{"root":"%TEMP%","include":["**"]}],"action":"delete","riskClass":"safe","consequence":"regenerated",
         "reversible":false,"requiresAdmin":false,"automationEligible":true,"sideEffects_ru":"s","verifiedOn":{"os":["10"]},"fixtures":["f"]}
        """;

    private static CleanupCatalog Load(string rule, int version = 1, int minimum = 0) =>
        CatalogLoader.Load(new MemoryStream(Encoding.UTF8.GetBytes($$"""{"version":{{version}},"rules":[{{rule}}]}""")), minimum);

    [Fact]
    public void Minimal_rule_loads() => Assert.Equal("x.y", Assert.Single(Load(Rule).Rules).Id);

    [Theory]
    [InlineData("\"%TEMP%\",\"include\":[\"**\"]", "\"%TEMP%\",\"include\":[\"..\\\\x\"]")]
    [InlineData("\"%TEMP%\",\"include\":[\"**\"]", "\"%TEMP%\",\"include\":[\"C:\\\\x\"]")]
    [InlineData("\"%TEMP%\",\"include\":[\"**\"]", "\"%TEMP%\",\"include\":[\"a/b\"]")]
    [InlineData("\"root\":\"%TEMP%\"", "\"root\":\"C:\\\\Windows\"")]
    [InlineData("\"root\":\"%TEMP%\"", "\"root\":\"%WINDIR%\"")]
    [InlineData("\"root\":\"%TEMP%\"", "\"root\":\"%TEMP%\\\\..\"")]
    [InlineData("\"root\":\"%TEMP%\"", "\"root\":\"{profile}\"")]
    [InlineData("\"action\":\"delete\"", "\"action\":\"shell\"")]
    [InlineData("\"consequence\":\"regenerated\"", "\"consequence\":\"lost\"")]
    [InlineData("\"riskClass\":\"safe\"", "\"riskClass\":\"yolo\"")]
    [InlineData("\"fixtures\":[\"f\"]", "\"fixtures\":[]")]
    [InlineData("\"id\":\"x.y\"", "\"id\":\"X Y\"")]
    [InlineData("\"title_ru\":\"t\",", "")]
    [InlineData("\"reversible\":false", "\"reversible\":false,\"command\":\"rm -rf\"")]
    public void Invalid_rule_is_rejected(string from, string to)
    {
        Assert.Contains(from, Rule);
        Assert.Throws<CatalogException>(() => Load(Rule.Replace(from, to)));
    }

    [Fact]
    public void Duplicate_ids_are_rejected() => Assert.Throws<CatalogException>(() => Load(Rule + "," + Rule));

    [Fact]
    public void Downgrade_is_rejected() => Assert.Throws<CatalogException>(() => Load(Rule, version: 3, minimum: 4));

    [Fact]
    public void Oversized_catalog_is_rejected() =>
        Assert.Throws<CatalogException>(() => CatalogLoader.Load(new MemoryStream(new byte[CatalogLoader.MaximumBytes + 1])));
}
