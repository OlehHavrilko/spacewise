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
        f.AllocatedBytes is long a ? DisplayFormat.Size(a) : Strings.Get("Common.Unavailable"),
        f.IsHardLinked ? Strings.Format("File.HardLink", f.Links) : "");
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
        Spacewise.Catalog.Consequence.Regenerated => Strings.Get("Cleanup.Regenerated"),
        Spacewise.Catalog.Consequence.Redownloaded => Strings.Get("Cleanup.Redownloaded"),
        _ => Strings.Get("Cleanup.Permanent"),
    };
    public string Note
    {
        get
        {
            RulePreview p = Preview;
            var notes = new List<string>();
            if (p.Rule.Action == SystemRules.RecycleBinAction) notes.Add(Strings.Get("Cleanup.NoQuarantine"));
            else if (p.Rule.RequiresAdmin) notes.Add(Strings.Get("Cleanup.AdminRequired"));
            else if (p.Rule.Action != "delete" && p.Rule.Action != SystemRules.RecycleBinAction) notes.Add(Strings.Get("Cleanup.SystemEstimate"));
            if (p.Running.Count > 0) notes.Add(Strings.Get("Cleanup.CloseRunning") + string.Join(", ", p.Running));
            if (p.LockedBy.Count > 0)
                notes.Add(Strings.Get("Cleanup.LockedFiles") + string.Join(", ", p.LockedBy.Take(5).Select(l => $"{l.Name} ({l.ProcessId})"))
                    + (p.LockedBy.Count > 5 ? Strings.Format("Cleanup.MoreLocks", p.LockedBy.Count - 5) : "")
                    + (p.LockChecked < p.Items.Count ? Strings.Format("Cleanup.LockCoverage", DisplayFormat.Count(p.LockChecked), DisplayFormat.Count(p.Items.Count)) : ""));
            if (p.TooRecent > 0) notes.Add(Strings.Format("Cleanup.RecentSkipped", DisplayFormat.Count(p.TooRecent)));
            if (p.Protected > 0) notes.Add(Strings.Format("Cleanup.ProtectedSkipped", DisplayFormat.Count(p.Protected)));
            if (p.Deferred > 0) notes.Add(Strings.Format("Cleanup.DeferredSkipped", DisplayFormat.Count(p.Deferred)));
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
    public string Kind => Usage.Program.Kind == ProgramKind.Steam ? Strings.Get("Usage.SteamGame") : Strings.Get("Usage.Program");
    public string Status { get; } = usage.Describe(now);
    public string Evidence { get; } = usage.Evidence switch
    {
        EvidenceLevel.Heuristic => Strings.Get("Usage.Heuristic"),
        EvidenceLevel.Corroborated => Strings.Get("Usage.Corroborated"),
        EvidenceLevel.DirectEvidence => Strings.Get("Usage.DirectEvidence"),
        _ => Strings.Get("Usage.NoEvidence"),
    };
    public bool IsUnused => Usage.Status == ObservationStatus.NoActivityObservedWithinCoveredWindow;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SizeText))] public partial long? SizeBytes { get; set; } = usage.Program.SizeBytes;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SizeText))] public partial bool Measured { get; set; }
    public string SizeText => SizeBytes is long b ? (Measured ? "" : Strings.Get("Common.Approximate")) + DisplayFormat.Size(b) : Strings.Get("Common.Unavailable");
}

// Asks the user before anything is moved; a view concern, so the view model sees only this seam.
public interface IConfirmation
{
    bool Confirm(string title, string message);
}

public sealed partial class MainViewModel(IScanService scanner, ScanStore history, CleanupPreviewer cleanup, PlanStore plans,
    ExecutionStore executions, PlanExecutor executor, QuarantineService quarantine, ExecutionRecovery recovery, IRecycleBin recycleBin, IVolumeSpace space,
    IConfirmation confirmation, IFileDialogs dialogs, IVolumeCatalog volumeCatalog, VolumeHistory volumeHistory, TimeProvider time) : ObservableObject
{
    public static string Limitations => Strings.Get("Scan.Limitations");

    [ObservableProperty] public partial string RootPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] public partial string ExclusionsText { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = Strings.Get("Scan.Ready");
    [ObservableProperty] public partial string Details { get; set; } = Limitations;
    [ObservableProperty] public partial IReadOnlyList<FileRow> Files { get; set; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectionSummary))] public partial IReadOnlyList<CleanupRow> Cleanup { get; set; } = [];
    [ObservableProperty] public partial string CleanupStatus { get; set; } = Strings.Get("Cleanup.NotEstimated");
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
        Status = Strings.Get("Scan.Running");
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                Status = Strings.Format("Scan.Progress", DisplayFormat.Count(p.Files), DisplayFormat.Size(p.LogicalBytes), DisplayFormat.Count(p.Skipped), DisplayFormat.Count(p.Errors));
                if (p.Roots is not null) RootsSummary = RootsText(p.Roots);
            });
            string[] excluded = ExclusionsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await scanner.ScanAsync(new(RootPath.Trim(), excluded), progress, cancellation);
            Files = result.LargestFiles.Select(FileRow.From).ToArray();
            AppLog.Info("Scan finished: state {State}, files {Files}, excluded {Excluded}, inaccessible {Inaccessible}", result.State, result.Files, result.ExcludedByPolicy, result.Inaccessible);
            Status = result.State == ScanState.RootNotFound
                ? Strings.Format("Scan.RootNotFound", result.Root)
                : Strings.Format("Scan.Summary", Display(result.State), DisplayFormat.Count(result.Files), DisplayFormat.Size(result.LogicalBytes));
            Details = Coverage(result) + Strings.Get("Scan.VolumeExplanation")
                + string.Join("\n", result.Diagnostics);
            // The cache is regenerable: a failed save is reported, not fatal.
            try { await history.SaveAsync(result, DateTime.UtcNow, CancellationToken.None); await history.TrimAsync(); }
            catch (Exception ex) { Details += Strings.Format("Scan.HistorySaveFailed", ex.Message); }
        }
        catch (Exception ex)
        {
            AppLog.Failure("Scan", ex);
            Status = Strings.Get("Scan.Failed");
            Details = ex.Message;
        }
        finally { IsBusy = false; }
    }

    // FR-SCAN-004: totals per top-level folder, shown while the scan runs.
    private static string RootsText(IEnumerable<RootAggregate> roots) => string.Join("\n", roots.Select(r =>
        Strings.Format("Scan.RootSummary", (r.Name.Length == 0 ? Strings.Get("Scan.RootFiles") : r.Name), DisplayFormat.Size(r.LogicalBytes), DisplayFormat.Count(r.Files))));

    // FR-CLN-002: the figure is an estimate of logical bytes, never presented as measured.
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PreviewCleanupAsync(CancellationToken cancellation)
    {
        Cleanup = [];
        CleanCommand.NotifyCanExecuteChanged();
        IsBusy = true;
        CleanupStatus = Strings.Get("Cleanup.Estimating");
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
                ? Strings.Get("Cleanup.NothingFound")
                : Strings.Format("Cleanup.EstimateSummary", DisplayFormat.Size(found.Sum(p => p.LogicalBytes)), DisplayFormat.Count(found.Length))
                  + Strings.Get("Cleanup.EstimateExplanation");
        }
        catch (OperationCanceledException) { CleanupStatus = Strings.Get("Cleanup.EstimateStopped"); }
        catch (Exception ex) { AppLog.Failure("Cleanup estimate", ex); CleanupStatus = Strings.Get("Cleanup.EstimateFailed") + ex.Message; }
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
            return Cleanup.Count == 0 ? "" : chosen.Length == 0 ? Strings.Get("Cleanup.SelectCategories") : Strings.Format("Cleanup.SelectionCount", DisplayFormat.Count(chosen.Length), DisplayFormat.Count(Cleanup.Count))
                + Strings.Format("Cleanup.SelectionEstimate", DisplayFormat.Size(chosen.Sum(r => r.Preview.LogicalBytes)));
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
            if (!decision.Allowed) { CleanupStatus = Strings.Get("Cleanup.Rejected") + decision.Reason; return; }
        }
        else if (bin is null) { CleanupStatus = Strings.Get("Cleanup.NoFiles"); return; }

        var message = new System.Text.StringBuilder();
        if (hasFiles)
            message.Append(Strings.Format("Cleanup.ConfirmFiles", DisplayFormat.Count(current.Items.Count), DisplayFormat.Size(current.ExpectedBytes)))
                .Append(Strings.Get("Cleanup.ConfirmRetention"))
                .Append(Strings.Get("Cleanup.ConfirmSkipped"));
        if (bin is not null)
            message.Append(Strings.Format("Cleanup.ConfirmBin", DisplayFormat.Count(bin.SystemCount), DisplayFormat.Size(bin.SystemBytes)))
                .Append(Strings.Get("Cleanup.ConfirmBinPermanent"));
        message.Append(Strings.Get("Common.Continue"));
        if (!confirmation.Confirm(Strings.Get("Cleanup.Title"), message.ToString())) { CleanupStatus = Strings.Get("Cleanup.Cancelled"); return; }

        IsBusy = true;
        CleanupStatus = Strings.Get("Cleanup.Running");
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
                lines.Add(Strings.Format("Cleanup.Quarantined", DisplayFormat.Count(report.Removed), DisplayFormat.Size(report.BytesRemoved))
                    + (report.Cancelled ? Strings.Get("Cleanup.StoppedByUser") : "")
                    + (report.Outcomes.Any(o => o.Status != RemoveStatus.Removed) ? Strings.Get("Cleanup.Skipped") + string.Join(", ", skipped) : "") + ".");
            }
            if (bin is not null && !cancellation.IsCancellationRequested) lines.Add(await EmptyRecycleBinAsync(bin));
            CleanupStatus = string.Join("\n", lines) + Strings.Get("Cleanup.EstimateStale");
            SetRows([]);
            await RefreshQuarantineAsync();
            await RefreshHistoryAsync();
        }
        catch (PlanRejectedException ex) { CleanupStatus = Strings.Get("Cleanup.Rejected") + ex.Message; }
        catch (Exception ex) { AppLog.Failure("Cleanup", ex); CleanupStatus = Strings.Get("Cleanup.Failed") + ex.Message; }
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
        return ok ? Strings.Get("RecycleBin.Emptied") + Measured(new PurgeReport((int)bin.SystemCount, 0, bin.SystemBytes, delta))
            : Strings.Get("RecycleBin.Failed");
    }

    private static string Skipped(RemoveStatus status) => status switch
    {
        RemoveStatus.Gone => Strings.Get("Skipped.Gone"), RemoveStatus.Changed => Strings.Get("Skipped.Changed"), RemoveStatus.Locked => Strings.Get("Skipped.Locked"),
        RemoveStatus.Unsafe => Strings.Get("Skipped.Unsafe"), RemoveStatus.Interrupted => Strings.Get("Skipped.Interrupted"), _ => Strings.Get("Skipped.Error"),
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
                    ? Strings.Format("Recovery.Summary", DisplayFormat.Count(recovered.NeverMoved), DisplayFormat.Count(recovered.Recovered), DisplayFormat.Count(recovered.Unknown)) : "")
                + (recovered.UnrecordedFiles > 0 ? Strings.Format("Recovery.Unrecorded", DisplayFormat.Count(recovered.UnrecordedFiles)) : "")
                + (expired.Purged > 0 ? Strings.Format("Quarantine.Expired", DisplayFormat.Count(expired.Purged)) + Measured(expired) + " " : ""));
            await RefreshHistoryAsync();
            await RefreshDisksAsync();
        }
        catch (Exception ex) { AppLog.Failure("Startup recovery", ex); QuarantineStatus = Strings.Get("Quarantine.Unavailable") + ex.Message; }
    }

    private async Task RefreshQuarantineAsync(string prefix = "")
    {
        IReadOnlyList<ExecutionStore.QuarantinedFile> held = await quarantine.HeldAsync();
        QuarantineStatus = prefix + (held.Count == 0 ? Strings.Get("Quarantine.Empty")
            : Strings.Format("Quarantine.Summary", DisplayFormat.Count(held.Count), DisplayFormat.Size(held.Sum(f => f.Size)), held.Min(f => f.ExpiresUtc).ToLocalTime()));
    }

    // FR-CLN-001..003: "Измерено" and "Оценка" stay separate; a gap over 10 % is explained.
    private static string Measured(PurgeReport r)
    {
        if (r.MeasuredFreeDelta is not long d) return Strings.Format("Space.Unmeasured", DisplayFormat.Size(r.LogicalBytes));
        string text = Strings.Format("Space.Measured", (d < 0 ? "−" : ""), DisplayFormat.Size(Math.Abs(d)), DisplayFormat.Size(r.LogicalBytes));
        return FreedSpace.WhyDifferent(r.LogicalBytes, d) is { } why ? text + Strings.Get("Space.WhyDifferent") + why : text;
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        IsBusy = true;
        try
        {
            RestoreReport report = await Task.Run(() => quarantine.RestoreAllAsync());
            AppLog.Info("Restore: restored {Restored}, failed {Failed}, hash mismatch {Mismatch}", report.Restored, report.Failed, report.HashMismatch);
            await RefreshQuarantineAsync(Strings.Format("Restore.Summary", DisplayFormat.Count(report.Restored))
                + (report.Failed > 0 ? Strings.Format("Restore.FailedItems", DisplayFormat.Count(report.Failed)) : "")
                + (report.HashMismatch > 0 ? Strings.Format("Restore.HashMismatch", DisplayFormat.Count(report.HashMismatch)) : "") + ". ");
        }
        catch (Exception ex) { AppLog.Failure("Restore", ex); QuarantineStatus = Strings.Get("Restore.Failed") + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PurgeAsync()
    {
        IsBusy = true;
        try
        {
            IReadOnlyList<ExecutionStore.QuarantinedFile> held = await quarantine.HeldAsync();
            if (held.Count == 0) { QuarantineStatus = Strings.Get("Quarantine.Empty"); return; }
            string message = Strings.Format("Quarantine.ConfirmPurge", DisplayFormat.Count(held.Count), DisplayFormat.Size(held.Sum(f => f.Size)))
                + Strings.Get("Quarantine.PurgePermanent");
            if (!confirmation.Confirm(Strings.Get("Quarantine.PurgeTitle"), message)) { QuarantineStatus = Strings.Get("Quarantine.PurgeCancelled"); return; }
            PurgeReport report = await Task.Run(() => quarantine.PurgeAllAsync());
            AppLog.Info("Quarantine purge: purged {Purged}, failed {Failed}, measured delta {Delta}", report.Purged, report.Failed, report.MeasuredFreeDelta);
            await RefreshQuarantineAsync(Strings.Format("Quarantine.Purged", DisplayFormat.Count(report.Purged))
                + (report.Failed > 0 ? Strings.Format("Quarantine.PurgeFailedItems", DisplayFormat.Count(report.Failed)) : "") + ". " + Measured(report) + " ");
        }
        catch (Exception ex) { AppLog.Failure("Quarantine purge", ex); QuarantineStatus = Strings.Get("Quarantine.PurgeFailed") + ex.Message; }
        finally { IsBusy = false; }
    }

    public sealed record HistoryRow(string Time, string Action, string Result, string Category, string Size, string Path);

    [ObservableProperty] public partial IReadOnlyList<HistoryRow> History { get; set; } = [];
    [ObservableProperty] public partial string HistoryStatus { get; set; } = "";
    [ObservableProperty] public partial bool ShowWelcome { get; set; }

    private static string ActionText(string action, string path) => path.StartsWith("shell:", StringComparison.Ordinal) ? Strings.Get("History.EmptyBin") : action switch
    {
        "restore" => Strings.Get("History.Restore"),
        "purge" => Strings.Get("History.Purge"),
        _ => Strings.Get("History.Cleanup"),
    };

    private static string ResultText(string result) => result switch
    {
        "Removed" => Strings.Get("Result.Removed"), "Gone" => Strings.Get("Result.Gone"), "Changed" => Strings.Get("Result.Changed"), "Locked" => Strings.Get("Result.Locked"),
        "Unsafe" => Strings.Get("Result.Unsafe"), "Failed" => Strings.Get("Result.Failed"), "Interrupted" => Strings.Get("Result.Interrupted"), "Done" => Strings.Get("Result.Done"), _ => result,
    };

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        try
        {
            var rows = await executions.RecentDecisionsAsync(500);
            History = [.. rows.Select(r => new HistoryRow(r.TimeUtc.ToLocalTime().ToString("g"), ActionText(r.Action, r.Path), ResultText(r.Result),
                r.Category, DisplayFormat.Size(r.Bytes), r.Path))];
            HistoryStatus = rows.Count == 0 ? Strings.Get("History.Empty") : Strings.Format("History.Recent", DisplayFormat.Count(rows.Count));
        }
        catch (Exception ex) { AppLog.Failure("History", ex); HistoryStatus = Strings.Get("History.ReadFailed") + ex.Message; }
    }

    // A5/A14: every decision row is chained to the previous one; an edited or removed row is found here.
    [RelayCommand]
    private async Task VerifyJournalAsync()
    {
        try
        {
            long? broken = await Task.Run(() => executions.VerifyJournalAsync());
            HistoryStatus = broken is null ? Strings.Get("History.Verified")
                : Strings.Format("History.Broken", broken);
            AppLog.Info("Journal verification: broken row {Row}", broken);
        }
        catch (Exception ex) { AppLog.Failure("Journal verification", ex); HistoryStatus = Strings.Get("History.VerifyFailed") + ex.Message; }
    }

    [ObservableProperty] public partial bool ExportIncludesPaths { get; set; }

    // FR-DATA-003: a Markdown report of the journal; paths only when the box is ticked.
    [RelayCommand]
    private async Task ExportReportAsync()
    {
        string? file = dialogs.SaveFile(Strings.Get("Report.SaveTitle"), $"spacewise-report-{time.GetUtcNow():yyyyMMdd}.md", Strings.Get("Report.Filter"));
        if (file is null) return;
        try
        {
            var rows = await executions.RecentDecisionsAsync(5000);
            long? broken = await Task.Run(() => executions.VerifyJournalAsync());
            await System.IO.File.WriteAllTextAsync(file, ReportExport.BuildMarkdown(rows, ExportIncludesPaths, time.GetUtcNow().UtcDateTime, broken), new System.Text.UTF8Encoding(false));
            HistoryStatus = Strings.Format("Report.Saved", DisplayFormat.Count(rows.Count), (ExportIncludesPaths ? Strings.Get("Report.PathsIncluded") : Strings.Get("Report.PathsHidden")));
            AppLog.Info("Report exported: {Rows} rows, paths {Paths}", rows.Count, ExportIncludesPaths);
        }
        catch (Exception ex) { AppLog.Failure("Report export", ex); HistoryStatus = Strings.Get("Report.Failed") + ex.Message; }
    }

    // Logs and a few facts, no journal and no paths.
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        string? file = dialogs.SaveFile(Strings.Get("Diagnostics.SaveTitle"), $"spacewise-diagnostics-{time.GetUtcNow():yyyyMMdd}.zip", Strings.Get("Diagnostics.Filter"));
        if (file is null) return;
        try
        {
            var facts = new Dictionary<string, string>
            {
                [Strings.Get("Diagnostics.Version")] = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "?",
                ["Windows"] = Environment.OSVersion.VersionString,
                [".NET"] = Environment.Version.ToString(),
                [Strings.Get("Diagnostics.GeneratedUtc")] = time.GetUtcNow().UtcDateTime.ToString("O"),
            };
            if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
            await Task.Run(() => ReportExport.CreateDiagnosticPackage(file, AppLog.Directory, facts));
            HistoryStatus = Strings.Get("Diagnostics.Saved");
        }
        catch (Exception ex) { AppLog.Failure("Diagnostics export", ex); HistoryStatus = Strings.Get("Diagnostics.Failed") + ex.Message; }
    }

    [RelayCommand]
    private async Task DismissWelcomeAsync()
    {
        ShowWelcome = false;
        try { await executions.SaveSettingAsync("onboarded", "true"); }
        catch (Exception ex) { AppLog.Failure("Onboarding flag", ex); }
    }

    public sealed record DiskRow(string Name, string Label, string FileSystem, string Total, string Free, string Used, string Forecast);

    [ObservableProperty] public partial IReadOnlyList<DiskRow> Disks { get; set; } = [];
    [ObservableProperty] public partial string DisksStatus { get; set; } = "";

    private static string ForecastText(FillForecast f, DateTime now) => f.Kind switch
    {
        ForecastKind.NotEnoughData => Strings.Format("Forecast.NotEnoughData", FillForecast.MinimumPoints, FillForecast.MinimumSpanDays),
        ForecastKind.NotShrinking => Strings.Get("Forecast.NotShrinking"),
        _ => Strings.Format("Forecast.Expected", now.AddDays(f.Days!.Value).ToLocalTime(), DisplayFormat.Count((long)f.Days.Value))
            + (f.HighDays is double high ? Strings.Format("Forecast.Range", DisplayFormat.Count((long)f.LowDays!.Value), DisplayFormat.Count((long)high)) : Strings.Format("Forecast.Minimum", DisplayFormat.Count((long)f.LowDays!.Value))) + ")",
    };

    // §5.4: a snapshot per volume on each start (at most one an hour), then a trend line over the last 90 days.
    private async Task RefreshDisksAsync()
    {
        try
        {
            DateTime now = time.GetUtcNow().UtcDateTime;
            IReadOnlyList<VolumeInfo> volumes = await Task.Run(volumeCatalog.Fixed);
            await volumeHistory.RecordAsync(volumes, now);
            var rows = new List<DiskRow>();
            foreach (VolumeInfo v in volumes)
            {
                FillForecast forecast = FillForecast.From(await volumeHistory.PointsAsync(v.VolumeGuid), now);
                rows.Add(new(v.Root, v.Label, v.FileSystem, DisplayFormat.Size(v.TotalBytes), DisplayFormat.Size(v.FreeBytes),
                    v.TotalBytes > 0 ? Strings.Format("Disk.UsedPercent", 100 - 100.0 * v.FreeBytes / v.TotalBytes) : Strings.Get("Common.Unavailable"), ForecastText(forecast, now)));
            }
            Disks = rows;
            DisksStatus = rows.Count == 0 ? Strings.Get("Disk.None") : Strings.Format("Disk.Summary", DisplayFormat.Count(rows.Count));
        }
        catch (Exception ex) { AppLog.Failure("Disks", ex); DisksStatus = Strings.Get("Disk.Failed") + ex.Message; }
    }

    // FR-USE-008: removes what Spacewise itself stored (volume history). Windows' own launch records are not touched.
    [RelayCommand]
    private async Task EraseHistoryAsync()
    {
        if (!confirmation.Confirm(Strings.Get("History.EraseTitle"), Strings.Get("History.EraseConfirmation")))
            return;
        try
        {
            int removed = await volumeHistory.EraseAsync();
            UsageRows = [];
            UsageSummary = "";
            UsageStatus = Strings.Format("History.Erased", DisplayFormat.Count(removed));
            AppLog.Info("History erased: {Snapshots} snapshots", removed);
            await RefreshDisksAsync();
        }
        catch (Exception ex) { AppLog.Failure("Erase history", ex); UsageStatus = Strings.Get("History.EraseFailed") + ex.Message; }
    }

    [ObservableProperty] public partial IReadOnlyList<UsageRow> UsageRows { get; set; } = [];
    [ObservableProperty] public partial string UsageStatus { get; set; } = Strings.Get("Usage.Instructions");
    [ObservableProperty] public partial string UsageSummary { get; set; } = "";
    private CancellationTokenSource? sizing;

    // FR-USE-001/002/005. Reads only the current user's UserAssist (FR-USE-008); nothing is stored.
    [RelayCommand]
    private async Task LoadUsageAsync()
    {
        sizing?.Cancel();
        UsageStatus = Strings.Get("Usage.Reading");
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
                ? Strings.Format("Usage.Readable", DisplayFormat.Count(rows.Length))
                : Strings.Format("Usage.Unreadable", DisplayFormat.Count(rows.Length));
            sizing = new CancellationTokenSource();
            _ = MeasureSizesAsync(rows, sizing.Token);
        }
        catch (Exception ex) { AppLog.Failure("Usage read", ex); UsageStatus = Strings.Get("Usage.Failed") + ex.Message; }
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
            ? Strings.Get("Usage.NoneUnused")
            : Strings.Format("Usage.UnusedSummary", DisplayFormat.Count(unused.Programs), UsageAnalyzer.DefaultThresholdDays, DisplayFormat.Size(unused.Bytes))
                + (unused.WithoutSize > 0 ? Strings.Format("Usage.UnknownSizes", DisplayFormat.Count(unused.WithoutSize)) : "")
                + Strings.Get("Usage.NoUninstall");
    }

    // Three separate coverage buckets (FR-SCAN-002) instead of one "skipped" number.
    private static string Coverage(ScanResult r)
    {
        string unvisited = DisplayFormat.Count(r.UnvisitedDirectories) + Strings.Get("Scan.FolderCount")
            + (r.StoppedEarly ? Strings.Get("Scan.StoppedRemainder") : "");
        return Strings.Format("Scan.Coverage", DisplayFormat.Count(r.ExcludedByPolicy), DisplayFormat.Count(r.Inaccessible), unvisited);
    }
    private static string Display(ScanState state) => state switch
    {
        ScanState.Completed => Strings.Get("Scan.Completed"), ScanState.Partial => Strings.Get("Scan.Partial"),
        ScanState.Cancelled => Strings.Get("Scan.Cancelled"), _ => Strings.Get("Scan.Error")
    };
}
