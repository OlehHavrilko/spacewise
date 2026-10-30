using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Spacewise.Tests.Unit;

public sealed class LocalizationTests
{
    private static string Desktop
    {
        get
        {
            string? root = AppContext.BaseDirectory;
            while (root is not null && !File.Exists(Path.Combine(root, "Spacewise.slnx"))) root = Path.GetDirectoryName(root);
            return Path.Combine(root ?? throw new InvalidOperationException("Repository not found"), "src", "Spacewise.Desktop");
        }
    }

    [Fact]
    public void Desktop_Russian_text_lives_in_the_resource_dictionary_not_code()
    {
        var hits = Directory.EnumerateFiles(Desktop, "*.cs")
            .SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, index)))
            .Where(row => !row.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(row => Regex.IsMatch(row.line, "[А-Яа-яЁё]"))
            .Select(row => $"{Path.GetFileName(row.file)}:{row.index + 1}: {row.line.Trim()}");
        Assert.Empty(hits);
    }

    [Fact]
    public void Every_code_resource_reference_exists_and_templates_preserve_format_specifiers()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var entries = XDocument.Load(Path.Combine(Desktop, "Resources", "Strings.ru-RU.xaml"))
            .Root!.Elements().ToDictionary(e => (string)e.Attribute(x + "Key")!, e => e.Value);
        foreach (string file in Directory.EnumerateFiles(Desktop, "*.cs"))
        foreach (Match reference in Regex.Matches(File.ReadAllText(file), "Strings\\.(?:Get|Format)\\(\"([^\"]+)\""))
            Assert.True(entries.ContainsKey(reference.Groups[1].Value), $"Missing resource {reference.Groups[1].Value}");
        foreach ((string key, string value) in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"Empty resource {key}");
            _ = System.Text.CompositeFormat.Parse(value);
        }
        foreach (string file in Directory.EnumerateFiles(Desktop, "*.xaml"))
        foreach (Match reference in Regex.Matches(File.ReadAllText(file), @"\{(?:DynamicResource|StaticResource) (Ui\.\d+)\}"))
            Assert.True(entries.ContainsKey(reference.Groups[1].Value), $"Missing XAML resource {reference.Groups[1].Value}");
        Assert.Contains("{2:d}", entries["Quarantine.Summary"]);
        Assert.Contains("{0:d}", entries["Forecast.Expected"]);
        Assert.Equal("12 %", string.Format(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), entries["Disk.UsedPercent"], 12.3));
    }
}
