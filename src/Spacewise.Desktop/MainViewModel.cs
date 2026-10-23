using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spacewise.Catalog;
using Spacewise.Core;
using Spacewise.Data;
using Spacewise.Platform.Windows;

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
    public bool CanSelect { get; } = preview.Rule is { Action: "delete", RequiresAdmin: false } && preview.Items.Count > 0
        || preview.Rule.Action == SystemRules.RecycleBinAction && preview.SystemCount > 0;
    // FR-UI-004: nothing is preselected; the user picks categories.
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public string Title => Preview.Rule.TitleRu;
    public string Files => DisplayFormat.Count(Preview.FileCount);
    public string Estimate => DisplayFormat.Size(Preview.LogicalBytes);
    public string SideEffects => Preview.Rule.SideEffectsRu;
    public string Explanation => RuleExplainer.Explain(Preview);
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
            if (p.Rule.Action == SystemRules.RecycleBinAction) notes.Add("удаляется окончательно, без карантина");
            else if (p.Rule.RequiresAdmin) notes.Add("нужны права администратора");
            else if (p.Rule.Action != "delete" && p.Rule.Action != SystemRules.RecycleBinAction) notes.Add("системный обработчик, пока только оценка");
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

// One program on the "Использование" tab. Size starts as the registry's estimate and is replaced by a measured one.
public sealed partial class UsageRow(ProgramUsage usage, DateTime now) : ObservableObject
{
    public ProgramUsage Usage { get; } = usage;
    public string Name => Usage.Program.Name;
    public string Publisher => Usage.Program.Publisher ?? "";
    public string Kind => Usage.Program.Kind == ProgramKind.Steam ? "Игра Steam" : "Программа";
    public string Status { get; } = usage.Describe(now);
    public string Evidence { get; } = usage.Evidence switch
    {
        EvidenceLevel.Heuristic => "Эвристика: только запуски через оболочку",
        EvidenceLevel.Corroborated => "Подтверждено несколькими источниками",
        EvidenceLevel.DirectEvidence => "Прямое свидетельство",
        _ => "Нет данных",
    };
    public bool IsUnused => Usage.Status == ObservationStatus.NoActivityObservedWithinCoveredWindow;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SizeText))] public partial long? SizeBytes { get; set; } = usage.Program.SizeBytes;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SizeText))] public partial bool Measured { get; set; }
    public string SizeText => SizeBytes is long b ? (Measured ? "" : "≈ ") + DisplayFormat.Size(b) : "—";
}

// Asks the user before anything is moved; a view concern, so the view model sees only this seam.
public interface IConfirmation
{
    bool Confirm(string title, string message);
}

public sealed partial class MainViewModel(IScanService scanner, ScanStore history, CleanupPreviewer cleanup, PlanStore plans,
    ExecutionStore executions, PlanExecutor executor, QuarantineService quarantine, ExecutionRecovery recovery, IRecycleBin recycleBin, IVolumeSpace space,
    IConfirmation confirmation, IFileDialogs dialogs, TimeProvider time) : ObservableObject
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
    [ObservableProperty] public partial string RootsSummary { get; set; } = "";


    // ScanCommand.IsRunning drives busy state; ScanCancelCommand is generated alongside it.
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken cancellation)
    {
        Files = [];
        RootsSummary = "";
        IsBusy = true;
        Status = "Анализ выполняется…";
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                Status = $"Файлов: {DisplayFormat.Count(p.Files)} · логически: {DisplayFormat.Size(p.LogicalBytes)} · пропуски: {DisplayFormat.Count(p.Skipped)} · ошибки: {DisplayFormat.Count(p.Errors)}";
                if (p.Roots is not null) RootsSummary = RootsText(p.Roots);
            });
            string[] excluded = ExclusionsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await scanner.ScanAsync(new(RootPath.Trim(), excluded), progress, cancellation);
            Files = result.LargestFiles.Select(FileRow.From).ToArray();
            AppLog.Info("Scan finished: state {State}, files {Files}, excluded {Excluded}, inaccessible {Inaccessible}", result.State, result.Files, result.ExcludedByPolicy, result.Inaccessible);
            Status = result.State == ScanState.RootNotFound
                ? $"Анализ не выполнен: папка не найдена — {result.Root}"
                : $"{Display(result.State)} · файлов: {DisplayFormat.Count(result.Files)} · логически: {DisplayFormat.Size(result.LogicalBytes)}";
            Details = Coverage(result) + "\nЭто логический объём записей, не уникальный физический объём. Место на диске показано только для крупнейших файлов. Модель отсутствует.\n"
                + string.Join("\n", result.Diagnostics);
            // The cache is regenerable: a failed save is reported, not fatal.
            try { await history.SaveAsync(result, DateTime.UtcNow, CancellationToken.None); await history.TrimAsync(); }
            catch (Exception ex) { Details += $"\nИстория сканов не сохранена: {ex.Message}"; }
        }
        catch (Exception ex)
        {
            AppLog.Failure("Scan", ex);
            Status = "Проверка не выполнена";
            Details = ex.Message;
        }
        finally { IsBusy = false; }
    }

    // FR-SCAN-004: totals per top-level folder, shown while the scan runs.
    private static string RootsText(IEnumerable<RootAggregate> roots) => string.Join("\n", roots.Select(r =>
        $"{(r.Name.Length == 0 ? "(файлы в корне)" : r.Name)} — {DisplayFormat.Size(r.LogicalBytes)}, файлов: {DisplayFormat.Count(r.Files)}"));

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
            var clock = System.Diagnostics.Stopwatch.StartNew();
            cleanup.Timings = (rule, files, ms) => AppLog.Info("Estimate rule {Rule}: {Files} files, {Milliseconds} ms", rule, files, ms);
            IReadOnlyList<RulePreview> previews = await Task.Run(() => cleanup.Preview(cancellation), cancellation);
            AppLog.Info("Cleanup estimate took {Milliseconds} ms", clock.ElapsedMilliseconds);
            RecycleBinInfo? binInfo = await Task.Run(recycleBin.Query, cancellation);
            RulePreview[] found = [.. previews.Where(p => p.Items.Count > 0)
                .Concat(binInfo is { Items: > 0 } ? [SystemRules.RecycleBinPreview(binInfo.Items, binInfo.Bytes)] : [])
                .OrderByDescending(p => p.LogicalBytes)];
            SetRows([.. found.Select(p => new CleanupRow(p))]);
            AppLog.Info("Cleanup estimate: {Categories} categories, {Files} files", found.Length, found.Sum(p => p.Items.Count));
            CleanupStatus = found.Length == 0
                ? "По правилам каталога ничего не найдено"
                : $"Оценка: {DisplayFormat.Size(found.Sum(p => p.LogicalBytes))} в {DisplayFormat.Count(found.Length)} категориях. "
                  + "Это сумма логических размеров; сколько освободится на диске, покажет измерение тома после очистки. Очистка переносит файлы в карантин.";
        }
        catch (OperationCanceledException) { CleanupStatus = "Оценка остановлена"; }
        catch (Exception ex) { AppLog.Failure("Cleanup estimate", ex); CleanupStatus = "Оценка не выполнена: " + ex.Message; }
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
            return Cleanup.Count == 0 ? "" : chosen.Length == 0 ? "Отметьте категории, которые нужно очистить" : $"Выбрано категорий: {DisplayFormat.Count(chosen.Length)} из {DisplayFormat.Count(Cleanup.Count)}, "
                + $"оценка {DisplayFormat.Size(chosen.Sum(r => r.Preview.LogicalBytes))}";
        }
    }

    [RelayCommand]
    private void SelectAll() { foreach (CleanupRow row in Cleanup.Where(r => r.CanSelect)) row.IsSelected = true; }

    [RelayCommand]
    private void SelectNone() { foreach (CleanupRow row in Cleanup) row.IsSelected = false; }

    private bool CanClean() => Cleanup.Any(r => r.IsSelected && r.CanSelect);

    // Files go to quarantine (reversible); the Recycle Bin, if chosen, is emptied by Windows (permanent, said in the confirmation).
    [RelayCommand(CanExecute = nameof(CanClean), IncludeCancelCommand = true)]
    private async Task CleanAsync(CancellationToken cancellation)
    {
        RulePreview[] chosen = [.. Cleanup.Where(r => r.IsSelected && r.CanSelect).Select(r => r.Preview)];
        RulePreview? bin = chosen.FirstOrDefault(p => p.Rule.Action == SystemRules.RecycleBinAction);
        CleanupPlan current = PlanBuilder.Build(chosen, time.GetUtcNow().UtcDateTime);
        bool hasFiles = current.Items.Count > 0;
        if (hasFiles)
        {
            PolicyDecision decision = MutationPolicy.Evaluate(current, time.GetUtcNow().UtcDateTime);
            if (!decision.Allowed) { CleanupStatus = "Очистка отклонена: " + decision.Reason; return; }
        }
        else if (bin is null) { CleanupStatus = "Очистка отклонена: в выбранных категориях нет файлов."; return; }

        var message = new System.Text.StringBuilder();
        if (hasFiles)
            message.Append($"В карантин будет перемещено файлов: {DisplayFormat.Count(current.Items.Count)} (логически {DisplayFormat.Size(current.ExpectedBytes)}). ")
                .Append("Файлы не стираются: их можно вернуть кнопкой «Вернуть из карантина» в течение 30 дней, потом они удаляются безвозвратно. ")
                .Append("Занятые и изменённые после оценки файлы будут пропущены. Место на диске освободится после очистки карантина.\n\n");
        if (bin is not null)
            message.Append($"Корзина будет очищена окончательно: объектов {DisplayFormat.Count(bin.SystemCount)} (около {DisplayFormat.Size(bin.SystemBytes)}). ")
                .Append("Вернуть их будет нельзя, карантин для Корзины не применяется.\n\n");
        message.Append("Продолжить?");
        if (!confirmation.Confirm("Очистка", message.ToString())) { CleanupStatus = "Очистка отменена"; return; }

        IsBusy = true;
        CleanupStatus = "Очистка выполняется…";
        try
        {
            var lines = new List<string>();
            if (hasFiles)
            {
                long planId = await plans.SaveAsync(current, cancellation: CancellationToken.None);
                ExecutionReport report = await Task.Run(() => executor.Execute(current, cancellation, executions.JournalFor(planId, time)), CancellationToken.None);
                await executions.FinishPlanAsync(planId, report.Cancelled ? "cancelled" : "done", CancellationToken.None);
                AppLog.Info("Cleanup finished: plan {PlanId}, quarantined {Removed} of {Total}, cancelled {Cancelled}", planId, report.Removed, report.Outcomes.Count, report.Cancelled);
                var skipped = report.Outcomes.Where(o => o.Status != RemoveStatus.Removed).GroupBy(o => o.Status).Select(g => $"{Skipped(g.Key)}: {DisplayFormat.Count(g.Count())}");
                lines.Add($"Перемещено в карантин: {DisplayFormat.Count(report.Removed)} файлов (оценка по логическим размерам: {DisplayFormat.Size(report.BytesRemoved)}). Освобождено на диске: 0 Б — файлы в карантине пока занимают место, оно освободится после очистки карантина"
                    + (report.Cancelled ? "; остановлено пользователем" : "")
                    + (report.Outcomes.Any(o => o.Status != RemoveStatus.Removed) ? "; пропущено — " + string.Join(", ", skipped) : "") + ".");
            }
            if (bin is not null && !cancellation.IsCancellationRequested) lines.Add(await EmptyRecycleBinAsync(bin));
            CleanupStatus = string.Join("\n", lines) + "\nОценка устарела, выполните её заново.";
            SetRows([]);
            await RefreshQuarantineAsync();
            await RefreshHistoryAsync();
        }
        catch (PlanRejectedException ex) { CleanupStatus = "Очистка отклонена: " + ex.Message; }
        catch (Exception ex) { AppLog.Failure("Cleanup", ex); CleanupStatus = "Очистка не выполнена: " + ex.Message; }
        finally { IsBusy = false; }
    }

    // FR-CLN-001..003: free space of every fixed volume is read before and after, and the shell's own count is the estimate.
    private async Task<string> EmptyRecycleBinAsync(RulePreview bin)
    {
        string[] volumes = [.. System.IO.DriveInfo.GetDrives().Where(d => d.DriveType == System.IO.DriveType.Fixed && d.IsReady).Select(d => d.Name)];
        long? Free() => volumes.Select(space.FreeBytes).Aggregate((long?)0, (sum, v) => sum is null || v is null ? null : sum + v);
        long? before = Free();
        bool ok = await Task.Run(recycleBin.Empty);
        long? after = Free();
        long? delta = before is long b && after is long a ? a - b : null;
        await executions.RecordSystemActionAsync(bin.Rule.Id, bin.SystemBytes, ok ? "Done" : "Failed", time.GetUtcNow().UtcDateTime);
        AppLog.Info("Recycle Bin emptied: ok {Ok}, items {Items}, measured delta {Delta}", ok, bin.SystemCount, delta);
        return ok ? "Корзина очищена. " + Measured(new PurgeReport((int)bin.SystemCount, 0, bin.SystemBytes, delta))
            : "Корзину очистить не удалось: Windows отказала.";
    }

    private static string Skipped(RemoveStatus status) => status switch
    {
        RemoveStatus.Gone => "уже нет", RemoveStatus.Changed => "изменены после оценки", RemoveStatus.Locked => "заняты",
        RemoveStatus.Unsafe => "отклонены защитой", RemoveStatus.Interrupted => "прерваны", _ => "ошибка",
    };

    // Called when the window opens: files past their retention are deleted for good, then the quarantine is summarised.
    public async Task InitializeAsync()
    {
        try
        {
            ShowWelcome = await executions.GetSettingAsync("onboarded") is null;
            RecoveryReport recovered = await recovery.RecoverAsync();
            PurgeReport expired = await Task.Run(() => quarantine.PurgeExpiredAsync());
            await RefreshQuarantineAsync((recovered.NeverMoved + recovered.Recovered + recovered.Unknown > 0
                    ? $"После прерванной очистки: не тронуто {DisplayFormat.Count(recovered.NeverMoved)}, найдено в карантине {DisplayFormat.Count(recovered.Recovered)}, неясно {DisplayFormat.Count(recovered.Unknown)}. " : "")
                + (recovered.UnrecordedFiles > 0 ? $"Неучтённых файлов в карантине: {DisplayFormat.Count(recovered.UnrecordedFiles)}. " : "")
                + (expired.Purged > 0 ? $"Удалено по сроку хранения: {DisplayFormat.Count(expired.Purged)}. " + Measured(expired) + " " : ""));
            await RefreshHistoryAsync();
        }
        catch (Exception ex) { AppLog.Failure("Startup recovery", ex); QuarantineStatus = "Карантин недоступен: " + ex.Message; }
    }

    private async Task RefreshQuarantineAsync(string prefix = "")
    {
        IReadOnlyList<ExecutionStore.QuarantinedFile> held = await quarantine.HeldAsync();
        QuarantineStatus = prefix + (held.Count == 0 ? "Карантин пуст"
            : $"В карантине: {DisplayFormat.Count(held.Count)} файлов, логически {DisplayFormat.Size(held.Sum(f => f.Size))}; самый ранний срок хранения до {held.Min(f => f.ExpiresUtc).ToLocalTime():d}");
    }

    // FR-CLN-001..003: "Измерено" and "Оценка" stay separate; a gap over 10 % is explained.
    private static string Measured(PurgeReport r)
    {
        if (r.MeasuredFreeDelta is not long d) return $"Оценка (логический размер): {DisplayFormat.Size(r.LogicalBytes)}. Освобождённое место измерить не удалось.";
        string text = $"Освобождено на диске (измерено): {(d < 0 ? "−" : "")}{DisplayFormat.Size(Math.Abs(d))}. Оценка (логический размер): {DisplayFormat.Size(r.LogicalBytes)}.";
        return FreedSpace.WhyDifferent(r.LogicalBytes, d) is { } why ? text + "\nПочему цифры отличаются: " + why : text;
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        IsBusy = true;
        try
        {
            RestoreReport report = await Task.Run(() => quarantine.RestoreAllAsync());
            AppLog.Info("Restore: restored {Restored}, failed {Failed}, hash mismatch {Mismatch}", report.Restored, report.Failed, report.HashMismatch);
            await RefreshQuarantineAsync($"Возвращено: {DisplayFormat.Count(report.Restored)}"
                + (report.Failed > 0 ? $"; не удалось: {DisplayFormat.Count(report.Failed)} (на месте уже есть файл или карантин недоступен)" : "")
                + (report.HashMismatch > 0 ? $"; не возвращено из-за несовпадения sha256: {DisplayFormat.Count(report.HashMismatch)} (файл остался в карантине)" : "") + ". ");
        }
        catch (Exception ex) { AppLog.Failure("Restore", ex); QuarantineStatus = "Возврат не выполнен: " + ex.Message; }
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
            AppLog.Info("Quarantine purge: purged {Purged}, failed {Failed}, measured delta {Delta}", report.Purged, report.Failed, report.MeasuredFreeDelta);
            await RefreshQuarantineAsync($"Удалено окончательно: {DisplayFormat.Count(report.Purged)}"
                + (report.Failed > 0 ? $"; не удалось: {DisplayFormat.Count(report.Failed)}" : "") + ". " + Measured(report) + " ");
        }
        catch (Exception ex) { AppLog.Failure("Quarantine purge", ex); QuarantineStatus = "Очистка карантина не выполнена: " + ex.Message; }
        finally { IsBusy = false; }
    }

    public sealed record HistoryRow(string Time, string Action, string Result, string Category, string Size, string Path);

    [ObservableProperty] public partial IReadOnlyList<HistoryRow> History { get; set; } = [];
    [ObservableProperty] public partial string HistoryStatus { get; set; } = "";
    [ObservableProperty] public partial bool ShowWelcome { get; set; }

    private static string ActionText(string action, string path) => path.StartsWith("shell:", StringComparison.Ordinal) ? "Очистка Корзины (окончательно)" : action switch
    {
        "restore" => "Возврат из карантина",
        "purge" => "Окончательное удаление из карантина",
        _ => "Очистка (перенос в карантин)",
    };

    private static string ResultText(string result) => result switch
    {
        "Removed" => "Перенесён", "Gone" => "Файла уже не было", "Changed" => "Изменён после оценки, пропущен", "Locked" => "Занят, пропущен",
        "Unsafe" => "Отклонено защитой", "Failed" => "Ошибка", "Interrupted" => "Прервано", "Done" => "Выполнено", _ => result,
    };

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        try
        {
            var rows = await executions.RecentDecisionsAsync(500);
            History = [.. rows.Select(r => new HistoryRow(r.TimeUtc.ToLocalTime().ToString("g"), ActionText(r.Action, r.Path), ResultText(r.Result),
                r.Category, DisplayFormat.Size(r.Bytes), r.Path))];
            HistoryStatus = rows.Count == 0 ? "Журнал пока пуст" : $"Последние записи: {DisplayFormat.Count(rows.Count)}";
        }
        catch (Exception ex) { AppLog.Failure("History", ex); HistoryStatus = "Журнал не прочитан: " + ex.Message; }
    }

    // A5/A14: every decision row is chained to the previous one; an edited or removed row is found here.
    [RelayCommand]
    private async Task VerifyJournalAsync()
    {
        try
        {
            long? broken = await Task.Run(() => executions.VerifyJournalAsync());
            HistoryStatus = broken is null ? "Цепочка журнала цела: записи не менялись и не удалялись."
                : $"Цепочка журнала нарушена, первая проблемная запись №{broken}. Журнал могли править вне Spacewise.";
            AppLog.Info("Journal verification: broken row {Row}", broken);
        }
        catch (Exception ex) { AppLog.Failure("Journal verification", ex); HistoryStatus = "Проверка не выполнена: " + ex.Message; }
    }

    [ObservableProperty] public partial bool ExportIncludesPaths { get; set; }

    // FR-DATA-003: a Markdown report of the journal; paths only when the box is ticked.
    [RelayCommand]
    private async Task ExportReportAsync()
    {
        string? file = dialogs.SaveFile("Сохранить отчёт", $"spacewise-report-{time.GetUtcNow():yyyyMMdd}.md", "Markdown (*.md)|*.md");
        if (file is null) return;
        try
        {
            var rows = await executions.RecentDecisionsAsync(5000);
            long? broken = await Task.Run(() => executions.VerifyJournalAsync());
            await System.IO.File.WriteAllTextAsync(file, ReportExport.BuildMarkdown(rows, ExportIncludesPaths, time.GetUtcNow().UtcDateTime, broken), new System.Text.UTF8Encoding(false));
            HistoryStatus = $"Отчёт сохранён: записей {DisplayFormat.Count(rows.Count)}, пути {(ExportIncludesPaths ? "включены" : "скрыты")}.";
            AppLog.Info("Report exported: {Rows} rows, paths {Paths}", rows.Count, ExportIncludesPaths);
        }
        catch (Exception ex) { AppLog.Failure("Report export", ex); HistoryStatus = "Отчёт не сохранён: " + ex.Message; }
    }

    // Logs and a few facts, no journal and no paths.
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        string? file = dialogs.SaveFile("Сохранить диагностический пакет", $"spacewise-diagnostics-{time.GetUtcNow():yyyyMMdd}.zip", "ZIP (*.zip)|*.zip");
        if (file is null) return;
        try
        {
            var facts = new Dictionary<string, string>
            {
                ["Версия"] = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "?",
                ["Windows"] = Environment.OSVersion.VersionString,
                [".NET"] = Environment.Version.ToString(),
                ["Сформирован (UTC)"] = time.GetUtcNow().UtcDateTime.ToString("O"),
            };
            if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
            await Task.Run(() => ReportExport.CreateDiagnosticPackage(file, AppLog.Directory, facts));
            HistoryStatus = "Диагностический пакет сохранён: журналы работы и сведения о версии, без списка файлов.";
        }
        catch (Exception ex) { AppLog.Failure("Diagnostics export", ex); HistoryStatus = "Пакет не сохранён: " + ex.Message; }
    }

    [RelayCommand]
    private async Task DismissWelcomeAsync()
    {
        ShowWelcome = false;
        try { await executions.SaveSettingAsync("onboarded", "true"); }
        catch (Exception ex) { AppLog.Failure("Onboarding flag", ex); }
    }

    [ObservableProperty] public partial IReadOnlyList<UsageRow> UsageRows { get; set; } = [];
    [ObservableProperty] public partial string UsageStatus { get; set; } = "Нажмите «Прочитать использование»: данные берутся из журнала запусков Windows текущего пользователя.";
    [ObservableProperty] public partial string UsageSummary { get; set; } = "";
    private CancellationTokenSource? sizing;

    // FR-USE-001/002/005. Reads only the current user's UserAssist (FR-USE-008); nothing is stored.
    [RelayCommand]
    private async Task LoadUsageAsync()
    {
        sizing?.Cancel();
        UsageStatus = "Чтение списка программ и журнала запусков…";
        try
        {
            DateTime now = time.GetUtcNow().UtcDateTime;
            (IReadOnlyList<ProgramUsage> usages, bool readable) = await Task.Run(() =>
            {
                IReadOnlyList<InstalledProgram> programs = ProgramInventory.Read();
                IReadOnlyList<UsageObservation>? observations = UserAssistReader.Read();
                return (UsageAnalyzer.Analyze(programs, observations, now), observations is not null);
            });
            UsageRow[] rows = [.. usages.Select(u => new UsageRow(u, now))
                .OrderByDescending(r => r.IsUnused).ThenByDescending(r => r.SizeBytes ?? 0).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
            UsageRows = rows;
            UpdateUsageSummary();
            UsageStatus = readable
                ? $"Программ: {DisplayFormat.Count(rows.Length)}. Источник: UserAssist (запуски через Пуск и Проводник); запуски другими способами он не видит."
                : $"Программ: {DisplayFormat.Count(rows.Length)}. Журнал запусков прочитать не удалось: история неизвестна, а не «не используется».";
            sizing = new CancellationTokenSource();
            _ = MeasureSizesAsync(rows, sizing.Token);
        }
        catch (Exception ex) { AppLog.Failure("Usage read", ex); UsageStatus = "Не удалось прочитать использование: " + ex.Message; }
    }

    // Folder sizes can take minutes on game libraries, so the list is shown first and sizes arrive one by one.
    private async Task MeasureSizesAsync(UsageRow[] rows, CancellationToken cancellation)
    {
        var tree = new FileSystemTree();
        try
        {
            foreach (UsageRow row in rows.Where(r => r.Usage.Program.InstallLocation is { Length: > 3 }))
            {
                cancellation.ThrowIfCancellationRequested();
                string location = row.Usage.Program.InstallLocation!;
                (long bytes, bool complete) = await Task.Run(() => System.IO.Directory.Exists(location)
                    ? ProgramInventory.Measure(location, tree, cancellation, maximumFiles: 200_000) : (-1L, true), cancellation);
                if (bytes < 0) continue;
                if (complete || bytes > (row.SizeBytes ?? 0)) { row.SizeBytes = bytes; row.Measured = complete; }
                UpdateUsageSummary();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateUsageSummary()
    {
        UnusedSummary unused = UsageAnalyzer.Unused(UsageRows.Select(r => r.Usage with { Program = r.Usage.Program with { SizeBytes = r.SizeBytes } }));
        UsageSummary = unused.Programs == 0
            ? "Нет программ, для которых данных достаточно, чтобы сказать «не запускалась»."
            : $"{DisplayFormat.Count(unused.Programs)} программ не запускались {UsageAnalyzer.DefaultThresholdDays}+ дней · {DisplayFormat.Size(unused.Bytes)}"
                + (unused.WithoutSize > 0 ? $" (размер неизвестен у {DisplayFormat.Count(unused.WithoutSize)})" : "")
                + ". Это подсказка: удаление программ Spacewise не выполняет.";
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
