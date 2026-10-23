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

// One catalog rule in the cleanup estimate. Only rules the executor can run by file list are selectable.
public sealed partial class CleanupRow(RulePreview preview) : ObservableObject
{
    public RulePreview Preview { get; } = preview;
    public bool CanSelect { get; } = preview.Rule is { Action: "delete", RequiresAdmin: false } && preview.Items.Count > 0;
    [ObservableProperty] public partial bool IsSelected { get; set; } = preview.Rule is { Action: "delete", RequiresAdmin: false } && preview.Items.Count > 0;
    public string Title => Preview.Rule.TitleRu;
    public string Files => DisplayFormat.Count(Preview.Items.Count);
    public string Estimate => DisplayFormat.Size(Preview.LogicalBytes);
    public string SideEffects => Preview.Rule.SideEffectsRu;
    public string Consequence => Preview.Rule.Consequence switch
    {
        Spacewise.Catalog.Consequence.Regenerated => "Создастся заново",
        Spacewise.Catalog.Consequence.Redownloaded => "Скачается заново",
        _ => "Удаляется безвозвратно",
    };
    public string Note
    {
        get
        {
            RulePreview p = Preview;
            var notes = new List<string>();
            if (p.Rule.RequiresAdmin) notes.Add("нужны права администратора");
            else if (p.Rule.Action != "delete") notes.Add("системный обработчик, пока только оценка");
            if (p.Running.Count > 0) notes.Add("запущено, закройте: " + string.Join(", ", p.Running));
            if (p.LockedBy.Count > 0)
                notes.Add("файлы заняты: " + string.Join(", ", p.LockedBy.Take(5).Select(l => $"{l.Name} ({l.ProcessId})"))
                    + (p.LockedBy.Count > 5 ? $" и ещё {p.LockedBy.Count - 5}" : "")
                    + (p.LockChecked < p.Items.Count ? $"; проверено {DisplayFormat.Count(p.LockChecked)} из {DisplayFormat.Count(p.Items.Count)}" : ""));
            if (p.TooRecent > 0) notes.Add($"свежих пропущено: {DisplayFormat.Count(p.TooRecent)}");
            if (p.Protected > 0) notes.Add($"защищённых пропущено: {DisplayFormat.Count(p.Protected)}");
            if (p.Deferred > 0) notes.Add($"ссылок и облачных пропущено: {DisplayFormat.Count(p.Deferred)}");
            return string.Join("; ", notes);
        }
    }
}

// Asks the user before anything is moved; a view concern, so the view model sees only this seam.
public interface IConfirmation
{
    bool Confirm(string title, string message);
}

public sealed partial class MainViewModel(IScanService scanner, ScanStore history, CleanupPreviewer cleanup, PlanStore plans,
    ExecutionStore executions, PlanExecutor executor, QuarantineService quarantine, IConfirmation confirmation, TimeProvider time) : ObservableObject
{
    public const string Limitations = "Объём — сумма логических размеров записей; hard links могут учитываться повторно. Место на диске и число ссылок известны только для ста крупнейших файлов. Освобождаемое место не измеряется. Ссылки и облачные placeholders пропускаются. Модель отключена. Это каркас, а не готовый очиститель.";

    [ObservableProperty] public partial string RootPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] public partial string ExclusionsText { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "Готов к проверке";
    [ObservableProperty] public partial string Details { get; set; } = Limitations;
    [ObservableProperty] public partial IReadOnlyList<FileRow> Files { get; set; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectionSummary))] public partial IReadOnlyList<CleanupRow> Cleanup { get; set; } = [];
    [ObservableProperty] public partial string CleanupStatus { get; set; } = "Оценка не выполнялась";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] public partial bool IsBusy { get; set; }
    public bool IsIdle => !IsBusy;
    [ObservableProperty] public partial string QuarantineStatus { get; set; } = "";


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
            Details = Coverage(result) + "\nЭто логический объём записей, не уникальный физический объём. Место на диске показано только для крупнейших файлов. Модель отсутствует.\n"
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
        CleanCommand.NotifyCanExecuteChanged();
        IsBusy = true;
        CleanupStatus = "Оценка выполняется…";
        try
        {
            IReadOnlyList<RulePreview> previews = await Task.Run(() => cleanup.Preview(cancellation), cancellation);
            RulePreview[] found = [.. previews.Where(p => p.Items.Count > 0).OrderByDescending(p => p.LogicalBytes)];
            SetRows([.. found.Select(p => new CleanupRow(p))]);
            CleanupStatus = found.Length == 0
                ? "По правилам каталога ничего не найдено"
                : $"Оценка: {DisplayFormat.Size(found.Sum(p => p.LogicalBytes))} в {DisplayFormat.Count(found.Length)} категориях. "
                  + "Это сумма логических размеров; сколько освободится на диске, покажет измерение тома после очистки. Очистка переносит файлы в карантин.";
        }
        catch (OperationCanceledException) { CleanupStatus = "Оценка остановлена"; }
        catch (Exception ex) { CleanupStatus = "Оценка не выполнена: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private void SetRows(CleanupRow[] rows)
    {
        foreach (CleanupRow row in Cleanup) row.PropertyChanged -= OnRowChanged;
        foreach (CleanupRow row in rows) row.PropertyChanged += OnRowChanged;
        Cleanup = rows;
        CleanCommand.NotifyCanExecuteChanged();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CleanupRow.IsSelected)) return;
        CleanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectionSummary));
    }

    public string SelectionSummary
    {
        get
        {
            CleanupRow[] chosen = [.. Cleanup.Where(r => r.IsSelected)];
            return Cleanup.Count == 0 ? "" : $"Выбрано категорий: {DisplayFormat.Count(chosen.Length)} из {DisplayFormat.Count(Cleanup.Count)}, "
                + $"оценка {DisplayFormat.Size(chosen.Sum(r => r.Preview.LogicalBytes))}";
        }
    }

    [RelayCommand]
    private void SelectAll() { foreach (CleanupRow row in Cleanup.Where(r => r.CanSelect)) row.IsSelected = true; }

    [RelayCommand]
    private void SelectNone() { foreach (CleanupRow row in Cleanup) row.IsSelected = false; }

    private bool CanClean() => Cleanup.Any(r => r.IsSelected && r.CanSelect);

    // Quarantine is the default and only method: files are moved, not erased, and can be put back.
    [RelayCommand(CanExecute = nameof(CanClean), IncludeCancelCommand = true)]
    private async Task CleanAsync(CancellationToken cancellation)
    {
        RulePreview[] chosen = [.. Cleanup.Where(r => r.IsSelected && r.CanSelect).Select(r => r.Preview)];
        CleanupPlan current = PlanBuilder.Build(chosen, time.GetUtcNow().UtcDateTime);
        PolicyDecision decision = MutationPolicy.Evaluate(current, time.GetUtcNow().UtcDateTime);
        if (!decision.Allowed) { CleanupStatus = "Очистка отклонена: " + decision.Reason; return; }
        string message = $"В карантин будет перемещено файлов: {DisplayFormat.Count(current.Items.Count)} (логически {DisplayFormat.Size(current.ExpectedBytes)}) "
            + $"из категорий: {DisplayFormat.Count(chosen.Length)}.\n\n"
            + "Файлы не стираются: их можно вернуть кнопкой «Вернуть из карантина» в течение 30 дней, потом они удаляются безвозвратно. Занятые и изменённые после оценки файлы будут пропущены. "
            + "Место на диске освободится после очистки карантина.\n\nПродолжить?";
        if (!confirmation.Confirm("Очистка", message)) { CleanupStatus = "Очистка отменена"; return; }

        IsBusy = true;
        CleanupStatus = "Очистка выполняется…";
        try
        {
            long planId = await plans.SaveAsync(current, cancellation: CancellationToken.None);
            ExecutionReport report = await Task.Run(() => executor.Execute(current, cancellation), CancellationToken.None);
            await executions.RecordAsync(planId, report, time.GetUtcNow().UtcDateTime, cancellation: CancellationToken.None);
            var skipped = report.Outcomes.Where(o => o.Status != RemoveStatus.Removed).GroupBy(o => o.Status).Select(g => $"{Skipped(g.Key)}: {DisplayFormat.Count(g.Count())}");
            CleanupStatus = $"В карантине: {DisplayFormat.Count(report.Removed)} файлов (логически {DisplayFormat.Size(report.BytesRemoved)})"
                + (report.Cancelled ? "; остановлено пользователем" : "")
                + (report.Outcomes.Any(o => o.Status != RemoveStatus.Removed) ? "; пропущено — " + string.Join(", ", skipped) : "")
                + ". Оценка устарела, выполните её заново.";
            SetRows([]);
            await RefreshQuarantineAsync();
        }
        catch (PlanRejectedException ex) { CleanupStatus = "Очистка отклонена: " + ex.Message; }
        catch (Exception ex) { CleanupStatus = "Очистка не выполнена: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private static string Skipped(RemoveStatus status) => status switch
    {
        RemoveStatus.Gone => "уже нет", RemoveStatus.Changed => "изменены после оценки", RemoveStatus.Locked => "заняты",
        RemoveStatus.Unsafe => "отклонены защитой", _ => "ошибка",
    };

    // Called when the window opens: files past their retention are deleted for good, then the quarantine is summarised.
    public async Task InitializeAsync()
    {
        try
        {
            PurgeReport expired = await Task.Run(() => quarantine.PurgeExpiredAsync());
            await RefreshQuarantineAsync(expired.Purged > 0 ? $"Удалено по сроку хранения: {DisplayFormat.Count(expired.Purged)}. " + Measured(expired) + " " : "");
        }
        catch (Exception ex) { QuarantineStatus = "Карантин недоступен: " + ex.Message; }
    }

    private async Task RefreshQuarantineAsync(string prefix = "")
    {
        IReadOnlyList<ExecutionStore.QuarantinedFile> held = await quarantine.HeldAsync();
        QuarantineStatus = prefix + (held.Count == 0 ? "Карантин пуст"
            : $"В карантине: {DisplayFormat.Count(held.Count)} файлов, логически {DisplayFormat.Size(held.Sum(f => f.Size))}; самый ранний срок хранения до {held.Min(f => f.ExpiresUtc).ToLocalTime():d}");
    }

    private static string Measured(PurgeReport r) => r.MeasuredFreeDelta is long d
        ? $"Свободное место на томе изменилось на {(d < 0 ? "−" : "+")}{DisplayFormat.Size(Math.Abs(d))} (измерено до и после; логически удалено {DisplayFormat.Size(r.LogicalBytes)})."
        : $"Логически удалено {DisplayFormat.Size(r.LogicalBytes)}; свободное место измерить не удалось.";

    [RelayCommand]
    private async Task RestoreAsync()
    {
        IsBusy = true;
        try
        {
            RestoreReport report = await Task.Run(() => quarantine.RestoreAllAsync());
            await RefreshQuarantineAsync($"Возвращено: {DisplayFormat.Count(report.Restored)}"
                + (report.Failed > 0 ? $"; не удалось: {DisplayFormat.Count(report.Failed)} (на месте уже есть файл или карантин недоступен)" : "")
                + (report.HashMismatch > 0 ? $"; не возвращено из-за несовпадения sha256: {DisplayFormat.Count(report.HashMismatch)} (файл остался в карантине)" : "") + ". ");
        }
        catch (Exception ex) { QuarantineStatus = "Возврат не выполнен: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PurgeAsync()
    {
        IsBusy = true;
        try
        {
            IReadOnlyList<ExecutionStore.QuarantinedFile> held = await quarantine.HeldAsync();
            if (held.Count == 0) { QuarantineStatus = "Карантин пуст"; return; }
            string message = $"Окончательно удалить из карантина файлов: {DisplayFormat.Count(held.Count)} (логически {DisplayFormat.Size(held.Sum(f => f.Size))})?\n\n"
                + "Вернуть их после этого будет нельзя.";
            if (!confirmation.Confirm("Очистка карантина", message)) { QuarantineStatus = "Очистка карантина отменена"; return; }
            PurgeReport report = await Task.Run(() => quarantine.PurgeAllAsync());
            await RefreshQuarantineAsync($"Удалено окончательно: {DisplayFormat.Count(report.Purged)}"
                + (report.Failed > 0 ? $"; не удалось: {DisplayFormat.Count(report.Failed)}" : "") + ". " + Measured(report) + " ");
        }
        catch (Exception ex) { QuarantineStatus = "Очистка карантина не выполнена: " + ex.Message; }
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
