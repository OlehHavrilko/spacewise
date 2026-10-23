using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Spacewise.Data;

// FR-DATA-003: the report lists what Spacewise did. File paths are left out unless the user asks for them, because a path
// can reveal names of documents, projects and people.
public static class ReportExport
{
    public static string BuildMarkdown(IReadOnlyList<ExecutionStore.DecisionRow> rows, bool includePaths, DateTime nowUtc, long? brokenRow)
    {
        var text = new StringBuilder();
        text.AppendLine("# Отчёт Spacewise");
        text.AppendLine();
        text.AppendLine($"Сформирован: {nowUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        text.AppendLine($"Записей: {rows.Count}");
        text.AppendLine(brokenRow is null ? "Цепочка журнала: цела." : $"Цепочка журнала: нарушена, первая проблемная запись №{brokenRow}.");
        text.AppendLine(includePaths ? "Пути к файлам: включены." : "Пути к файлам: скрыты.");
        text.AppendLine();
        text.AppendLine("| Время | Действие | Результат | Категория | Размер, байт | Файл |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (ExecutionStore.DecisionRow row in rows)
            text.AppendLine($"| {row.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm} | {Cell(row.Action)} | {Cell(row.Result)} | {Cell(row.Category)} | {row.Bytes.ToString(CultureInfo.InvariantCulture)} | {(includePaths ? Cell(row.Path) : $"запись №{row.Id}")} |");
        return text.ToString();
    }

    // A pipe or line break inside a cell would break the table.
    private static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    // Logs (no paths at Information level), versions and counts. The journal and its data never go into the package.
    public static void CreateDiagnosticPackage(string zipPath, string logsDirectory, IReadOnlyDictionary<string, string> facts)
    {
        using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        ZipArchiveEntry info = zip.CreateEntry("info.txt");
        using (var writer = new StreamWriter(info.Open(), new UTF8Encoding(false)))
            foreach ((string key, string value) in facts) writer.WriteLine($"{key}: {value}");
        if (!Directory.Exists(logsDirectory)) return;
        foreach (string log in Directory.EnumerateFiles(logsDirectory, "spacewise-*.log"))
        {
            ZipArchiveEntry entry = zip.CreateEntry("logs/" + Path.GetFileName(log));
            // The running app holds the current log open for writing.
            using var source = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream target = entry.Open();
            source.CopyTo(target);
        }
    }
}
