using System.IO.Compression;
using Spacewise.Data;

namespace Spacewise.Tests.Unit;

public sealed class ReportExportTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "SpacewiseExp-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    public ReportExportTests() => Directory.CreateDirectory(dir);

    // Only files the tests created in their own directory; no recursion.
    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
        foreach (string sub in Directory.GetDirectories(dir))
        {
            foreach (string file in Directory.GetFiles(sub)) File.Delete(file);
            Directory.Delete(sub);
        }
        Directory.Delete(dir);
    }

    private static ExecutionStore.DecisionRow Row(long id, string path) =>
        new(id, Now, "approve", "Removed", "dev.npm.cache", 1234, path);

    [Fact]
    public void Paths_are_hidden_by_default_and_shown_on_request()
    {
        var rows = new[] { Row(7, @"C:\Users\anna\Documents\Secret plan.docx") };
        string hidden = ReportExport.BuildMarkdown(rows, includePaths: false, Now, null);
        Assert.DoesNotContain("Secret", hidden);
        Assert.DoesNotContain(@"C:\", hidden);
        Assert.Contains("запись №7", hidden);

        string shown = ReportExport.BuildMarkdown(rows, includePaths: true, Now, null);
        Assert.Contains(@"C:\Users\anna\Documents\Secret plan.docx", shown);
    }

    [Fact]
    public void Report_states_journal_integrity_and_survives_pipes_in_names()
    {
        string intact = ReportExport.BuildMarkdown([Row(1, "a|b")], true, Now, null);
        Assert.Contains("Цепочка журнала: цела", intact);
        Assert.Contains(@"a\|b", intact);
        Assert.Contains("нарушена, первая проблемная запись №4", ReportExport.BuildMarkdown([], false, Now, 4));
    }

    [Fact]
    public void Diagnostic_package_holds_logs_and_facts_but_nothing_else()
    {
        string logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "spacewise-20261023.log"), "started");
        File.WriteAllText(Path.Combine(logs, "journal.db"), "secret");
        string zipPath = Path.Combine(dir, "diag.zip");

        ReportExport.CreateDiagnosticPackage(zipPath, logs, new Dictionary<string, string> { ["Версия"] = "0.1.0" });

        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(["info.txt", "logs/spacewise-20261023.log"], zip.Entries.Select(e => e.FullName).Order());
        using var info = new StreamReader(zip.GetEntry("info.txt")!.Open());
        Assert.Contains("Версия: 0.1.0", info.ReadToEnd());
    }

    [Fact]
    public void Diagnostic_package_without_a_log_folder_still_has_the_facts()
    {
        string zipPath = Path.Combine(dir, "diag2.zip");
        ReportExport.CreateDiagnosticPackage(zipPath, Path.Combine(dir, "missing"), new Dictionary<string, string> { ["a"] = "b" });
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(["info.txt"], zip.Entries.Select(e => e.FullName));
    }
}
