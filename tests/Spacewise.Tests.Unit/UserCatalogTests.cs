using System.Text.Json;
using Spacewise.Catalog;
using Spacewise.Core;

namespace Spacewise.Tests.Unit;

public sealed class UserCatalogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SpacewiseRules-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void User_rule_is_untrusted_and_cannot_override_built_in()
    {
        Directory.CreateDirectory(directory);
        CleanupCatalog builtIn = CatalogLoader.LoadBuiltIn();
        CleanupRule original = builtIn.Rules[0];
        string file = Path.Combine(directory, "custom.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new CleanupCatalog(1, [original with { Id = "custom.example" }]), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        CleanupRule custom = CatalogLoader.LoadWithUserRules(directory).Rules.Single(r => r.Id == "custom.example");
        Assert.Equal(RiskClass.Expert, custom.RiskClass); Assert.Equal(Consequence.Lost, custom.Consequence); Assert.False(custom.AutomationEligible);
        File.WriteAllText(file, JsonSerializer.Serialize(new CleanupCatalog(1, [original]), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Throws<CatalogException>(() => CatalogLoader.LoadWithUserRules(directory));
    }
    [Theory]
    [InlineData("**/*.tmp", "file.tmp", true)]
    [InlineData("**/*.tmp", "one/two/file.tmp", true)]
    [InlineData("*.tmp", "one/file.tmp", false)]
    [InlineData("A/*", "a/file", false)]
    public void Posix_glob_respects_segments_and_case(string pattern, string path, bool expected) => Assert.Equal(expected, new RelativeGlob(pattern).Matches(path));
    [Theory]
    [InlineData("../*")][InlineData("/tmp/*")][InlineData("foo/../*")]
    public void Glob_rejects_traversal(string pattern) => Assert.Throws<ArgumentException>(() => new RelativeGlob(pattern));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
