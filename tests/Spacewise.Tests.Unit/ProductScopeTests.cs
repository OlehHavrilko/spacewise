using System.Text.RegularExpressions;

namespace Spacewise.Tests.Unit;

// A13: the product has no registry cleaning, RAM optimisation, SSD defragmentation or health score (spec §2, FR-UI-004).
// The check reads what users and the catalog actually see: XAML, user-visible strings in C#, the built-in catalog.
public partial class ProductScopeTests
{
    private static string Root
    {
        get
        {
            string? dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "Spacewise.slnx"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Spacewise.slnx not found above the test output.");
        }
    }

    private static IEnumerable<(string File, string Text)> UserFacingSources() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\") && (f.EndsWith(".xaml") || f.EndsWith(".cs") || f.EndsWith("catalog.json")))
            .Select(f => (f, File.ReadAllText(f)));

    [GeneratedRegex(@"реестр|дефрагментац|оптимизац\w*\s+(памяти|ОЗУ|RAM)|освобожден\w*\s+(памяти|ОЗУ|RAM)|индекс\s+здоровья|health\s*score|ускор\w*\s+(компьютер|ПК|систем)", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();

    [Fact]
    public void Forbidden_features_are_not_offered_anywhere_in_the_product()
    {
        var hits = UserFacingSources()
            .SelectMany(s => Forbidden().Matches(s.Text).Select(m => $"{Path.GetFileName(s.File)}: {m.Value}"))
            .Distinct();
        Assert.Empty(hits);
    }

    [Fact]
    public void No_clean_all_button_and_no_preselected_category()
    {
        string xaml = File.ReadAllText(Path.Combine(Root, "src", "Spacewise.Desktop", "MainWindow.xaml"));
        Assert.DoesNotMatch(@"Content=""(Очистить всё|Очистить все|Clean all)""", xaml);
        string model = File.ReadAllText(Path.Combine(Root, "src", "Spacewise.Desktop", "MainViewModel.cs"));
        // FR-UI-004: IsSelected starts false; only the explicit "Выбрать всё" command sets it.
        Assert.Matches(@"public partial bool IsSelected \{ get; set; \}\s*\r?\n", model);
        Assert.DoesNotMatch(@"IsSelected \{ get; set; \}\s*=\s*true", model);
    }
}
