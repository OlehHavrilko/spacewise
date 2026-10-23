using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spacewise.Catalog;
using Spacewise.Core;
using Spacewise.Data;

namespace Spacewise.Desktop;

public sealed record FileRow(string Path, string Size, string OnDisk, string Note)
{
    public static FileRow From(FileFinding f) => new(f.Path, DisplayFormat.Size(f.LogicalBytes),
        f.AllocatedBytes is long a ? DisplayFormat.Size(a) : "—",
        f.IsHardLinked ? $"Жёсткая ссылка ({f.Links}): 0 байт выгоды" : "");
}

// One catalog rule in the cleanup estimate. Nothing here deletes: the executor is a later step.
public sealed record CleanupRow(string Title, string Files, string Estimate, string Consequence, string Note, string SideEffects)
{
    public static CleanupRow From(RulePreview p)
    {
        var notes = new List<string>();
        if (p.Rule.RequiresAdmin) notes.Add("нужны права администратора");
        if (p.Running.Count > 0) notes.Add("запущено, закройте: " + string.Join(", ", p.Running));
        if (p.LockedBy.Count > 0)
            notes.Add("файлы заняты: " + string.Join(", ", p.LockedBy.Take(5).Select(l => $"{l.Name} ({l.ProcessId})"))
                + (p.LockedBy.Count > 5 ? $" и ещё {p.LockedBy.Count - 5}" : "")
                + (p.LockChecked < p.Items.Count ? $"; проверено {DisplayFormat.Count(p.LockChecked)} из {DisplayFormat.Count(p.Items.Count)}" : ""));
        if (p.TooRecent > 0) notes.Add($"свежих пропущено: {DisplayFormat.Count(p.TooRecent)}");
        if (p.Protected > 0) notes.Add($"защищённых пропущено: {DisplayFormat.Count(p.Protected)}");
        if (p.Deferred > 0) notes.Add($"ссылок и облачных пропущено: {DisplayFormat.Count(p.Deferred)}");
        string consequence = p.Rule.Consequence switch
        {
            Spacewise.Catalog.Consequence.Regenerated => "Создастся заново",
            Spacewise.Catalog.Consequence.Redownloaded => "Скачается заново",
            _ => "Удаляется безвозвратно",
        };
        return new(p.Rule.TitleRu, DisplayFormat.Count(p.Items.Count), DisplayFormat.Size(p.LogicalBytes), consequence,
            string.Join("; ", notes), p.Rule.SideEffectsRu);
    }
}

public sealed partial class MainViewModel(IScanService scanner, ScanStore history, CleanupPreviewer cleanup) : ObservableObject
{
    public const string Limitations = "Объём — сумма логических размеров записей; hard links могут учитываться повторно. Место на диске и число ссылок известны только для ста крупнейших файлов. Освобождаемое место не измеряется. Ссылки и облачные placeholders пропускаются. Модель отключена. Это каркас, а не готовый очиститель.";

    [ObservableProperty] public partial string RootPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] public partial string ExclusionsText { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "Готов к проверке";
    [ObservableProperty] public partial string Details { get; set; } = Limitations;
    [ObservableProperty] public partial IReadOnlyList<FileRow> Files { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<CleanupRow> Cleanup { get; set; } = [];
    [ObservableProperty] public partial string CleanupStatus { get; set; } = "Оценка не выполнялась";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] public partial bool IsBusy { get; set; }
    public bool IsIdle => !IsBusy;

    // ScanCommand.IsRunning drives busy state; ScanCancelCommand is generated alongside it.
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken cancellation)
    {
        Files = [];
        IsBusy = true;
        Status = "Анализ выполняется…";
        try
        {
            var progress = new Progress<ScanProgress>(p => Status = $"Файлов: {DisplayFormat.Count(p.Files)} · логически: {DisplayFormat.Size(p.LogicalBytes)} · пропуски: {DisplayFormat.Count(p.Skipped)} · ошибки: {DisplayFormat.Count(p.Errors)}");
            string[] excluded = ExclusionsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await scanner.ScanAsync(new(RootPath.Trim(), excluded), progress, cancellation);
            Files = result.LargestFiles.Select(FileRow.From).ToArray();
            Status = result.State == ScanState.RootNotFound
                ? $"Анализ не выполнен: папка не найдена — {result.Root}"
                : $"{Display(result.State)} · файлов: {DisplayFormat.Count(result.Files)} · логически: {DisplayFormat.Size(result.LogicalBytes)}";
            Details = Coverage(result) + "\nЭто логический объём записей, не уникальный физический объём. Место на диске показано только для крупнейших файлов. Очистка и модель отсутствуют.\n"
                + string.Join("\n", result.Diagnostics);
            // The cache is regenerable: a failed save is reported, not fatal.
            try { await history.SaveAsync(result, DateTime.UtcNow, CancellationToken.None); }
            catch (Exception ex) { Details += $"\nИстория сканов не сохранена: {ex.Message}"; }
        }
        catch (Exception ex)
        {
            Status = "Проверка не выполнена";
            Details = ex.Message;
        }
        finally { IsBusy = false; }
    }

    // FR-CLN-002: the figure is an estimate of logical bytes, never presented as measured.
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PreviewCleanupAsync(CancellationToken cancellation)
    {
        Cleanup = [];
        IsBusy = true;
        CleanupStatus = "Оценка выполняется…";
        try
        {
            IReadOnlyList<RulePreview> previews = await Task.Run(() => cleanup.Preview(cancellation), cancellation);
            RulePreview[] found = [.. previews.Where(p => p.Items.Count > 0).OrderByDescending(p => p.LogicalBytes)];
            Cleanup = [.. found.Select(CleanupRow.From)];
            CleanupStatus = found.Length == 0
                ? "По правилам каталога ничего не найдено"
                : $"Оценка: {DisplayFormat.Size(found.Sum(p => p.LogicalBytes))} в {DisplayFormat.Count(found.Length)} категориях. "
                  + "Это сумма логических размеров; сколько освободится на диске, покажет измерение тома после очистки. Удаление пока недоступно.";
        }
        catch (OperationCanceledException) { CleanupStatus = "Оценка остановлена"; }
        catch (Exception ex) { CleanupStatus = "Оценка не выполнена: " + ex.Message; }
        finally { IsBusy = false; }
    }

    // Three separate coverage buckets (FR-SCAN-002) instead of one "skipped" number.
    private static string Coverage(ScanResult r)
    {
        string unvisited = DisplayFormat.Count(r.UnvisitedDirectories) + " папок"
            + (r.StoppedEarly ? " и остаток после остановки" : "");
        return $"Исключено правилами: {DisplayFormat.Count(r.ExcludedByPolicy)} · Недоступно: {DisplayFormat.Count(r.Inaccessible)} · Не досканировано: {unvisited}";
    }
    private static string Display(ScanState state) => state switch
    {
        ScanState.Completed => "Проверка завершена", ScanState.Partial => "Проверка частичная",
        ScanState.Cancelled => "Остановлено; показана проверенная часть", _ => "Ошибка проверки"
    };
}
