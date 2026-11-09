using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using Spacewise.Catalog;
using Spacewise.Core;
using Spacewise.Data;
using Spacewise.Inference;
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
    public long LogicalBytes { get; } = preview.LogicalBytes;
    public bool CanSelect { get; } = preview.Rule is { Action: "delete", RequiresAdmin: false } && preview.Items.Count > 0
        || preview.Rule.Action == SystemRules.RecycleBinAction && preview.SystemCount > 0;
    // FR-UI-004: nothing is preselected; the user picks categories.
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public string Title => Preview.Rule.TitleRu;
    public string Files => DisplayFormat.Count(Preview.FileCount);
    public string Estimate => DisplayFormat.Size(LogicalBytes);
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

// Asks the user before cleanup; a view concern, so the view model sees only this seam.
public interface IConfirmation
{
    bool Confirm(string title, string message);
}

public sealed partial class MainViewModel(IScanService scanner, ScanStore history, CleanupPreviewer cleanup, PlanStore plans,
    ExecutionStore executions, PlanExecutor executor, QuarantineService quarantine, ExecutionRecovery recovery, IRecycleBin recycleBin, IVolumeSpace space,
    IConfirmation confirmation, IFileDialogs dialogs, IVolumeCatalog volumeCatalog, VolumeHistory volumeHistory, TimeProvider time,
    PinnedModelInstaller modelInstaller, AssistantProviderRegistry assistants, PersistentExclusions exclusions, FolderHistoryStore folderHistory,
    UserFolders userFolders, ICleanupPathPolicy cleanupPathPolicy) : ObservableObject
{
    public static string Limitations => Strings.Get("Scan.Limitations");
    private ScanResult? lastScan;
    [ObservableProperty] public partial IReadOnlyList<string> FolderTrendPaths { get; set; } = [];
    [ObservableProperty] public partial string? SelectedTrendPath { get; set; }
    [ObservableProperty] public partial IReadOnlyList<FolderTrendPoint> FolderTrendPoints { get; set; } = [];
    partial void OnSelectedTrendPathChanged(string? value) => _ = LoadFolderTrendAsync(value);
    private async Task LoadFolderTrendAsync(string? path)
    {
        try
        {
            IReadOnlyList<FolderTrendPoint> points = path is null ? [] : await folderHistory.PointsAsync(path);
            if (SelectedTrendPath == path) FolderTrendPoints = points;
        }
        catch (Exception e) { AppLog.Failure("Folder trend", e); }
    }
    [ObservableProperty] public partial string DuplicateStatus { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<string> DuplicateRows { get; set; } = [];

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task FindDuplicatesAsync(CancellationToken cancellation)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            SaveExclusions();
            DuplicateStatus = Strings.Get("Duplicates.Running");
            DuplicateReport report = await new DuplicateFinder(new WindowsDuplicateSource()).FindAsync(RootPath.Trim(), [.. exclusions.Paths, DataFolder], cancellation);
            DuplicateRows = report.Groups.Select(g => $"{DisplayFormat.Size(g.Files[0].Size)} × {g.Files.Count}\n" + string.Join("\n", g.Files.Select(f => f.Path))).ToArray();
            DuplicateStatus = Strings.Format("Duplicates.Result", report.Groups.Count, report.Candidates, DisplayFormat.Size(report.BytesRead), report.Skipped)
                + Strings.Get(report.Limited ? "Duplicates.Limited" : "Duplicates.Coverage");
        }
        catch (OperationCanceledException) { DuplicateStatus = Strings.Get("Duplicates.Cancelled"); }
        catch (Exception e) { AppLog.Failure("Duplicate analysis", e); DuplicateStatus = Strings.Get("Duplicates.Failed") + e.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ExportAnalysisReportAsync()
    {
        if (IsBusy || lastScan is null) return;
        string? file = dialogs.SaveFile(Strings.Get("Analysis.ExportTitle"), "spacewise-scan.html", "HTML (*.html)|*.html");
        if (file is null) return;
        IsBusy = true;
        try
        {
            string html = ScanHtmlReport.Build(lastScan, time.GetUtcNow().UtcDateTime, ExportIncludesPaths);
            await System.IO.File.WriteAllTextAsync(file, html, new System.Text.UTF8Encoding(false));
            Status = Strings.Get("Analysis.ExportSaved");
        }
        catch (Exception e) { AppLog.Failure("Analysis export", e); Status = Strings.Get("Analysis.ExportFailed") + e.Message; }
        finally { IsBusy = false; }
    }

    // Settings tab
    [ObservableProperty] public partial int ThemeIndex { get; set; } = (int)ThemeService.Current;
    partial void OnThemeIndexChanged(int value)
    {
        var choice = (ThemeChoice)value;
        ThemeService.Apply(choice);
        (AppSettings.Load() with { Theme = choice }).Save();
    }
    public string AppVersion { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstallModel), nameof(CanExplainCleanup))]
    public partial bool AssistantEnabled { get; set; } = AppSettings.Load().AssistantEnabled;
    public string AssistantModelName => modelInstaller.Model.Id;
    partial void OnAssistantEnabledChanged(bool value)
    {
        (AppSettings.Load() with { AssistantEnabled = value }).Save();
        if (!value) AssistantStatus = Strings.Get("AI.Disabled");
    }
    public string DataFolder { get; } = AppPaths.Data;
    [RelayCommand] private void OpenDataFolder() => OpenFolder(DataFolder);
    [RelayCommand] private void OpenLogsFolder() => OpenFolder(System.IO.Path.Combine(DataFolder, "logs"));
    [RelayCommand] private void OpenRulesFolder() => OpenFolder(System.IO.Path.Combine(DataFolder, "rules"));
    private static void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* explorer unavailable: nothing to recover */ }
    }
    [ObservableProperty] public partial string RootPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] public partial string ExclusionsText { get; set; } = string.Join(Environment.NewLine, exclusions.Paths);
    private void SaveExclusions() => exclusions.Update(ExclusionsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    [ObservableProperty] public partial string Status { get; set; } = Strings.Get("Scan.Ready");
    [ObservableProperty] public partial string Details { get; set; } = Limitations;
    [ObservableProperty] public partial IReadOnlyList<FileRow> Files { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<FolderNode> FolderRoots { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<FolderNode> MapFolders { get; set; } = [];
    [ObservableProperty] public partial FolderNode? SelectedFolder { get; set; }
    [ObservableProperty] public partial FolderNode? SelectedMapFolder { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ExplainSelectedFileCommand))]
    public partial FileRow? SelectedFile { get; set; }
    [ObservableProperty] public partial IReadOnlyList<FileRow> VisibleFiles { get; set; } = [];
    [ObservableProperty] public partial string AnalysisCoverage { get; set; } = Strings.Get("Analysis.Empty");
    [ObservableProperty] public partial string ScanComparison { get; set; } = "";
    [ObservableProperty] public partial string CleanupReport { get; set; } = "";
    [ObservableProperty] public partial string FeedbackStatus { get; set; } = "";
    private string? feedbackKey;
    private bool feedbackRecorded;
    public bool HasCleanupReport => !string.IsNullOrEmpty(CleanupReport);
    partial void OnCleanupReportChanged(string value)
    {
        OnPropertyChanged(nameof(HasCleanupReport));
        SaveFeedbackCommand.NotifyCanExecuteChanged();
    }
    private bool CanRecordFeedback => IsIdle && HasCleanupReport && feedbackKey is not null && !feedbackRecorded;

    [RelayCommand(CanExecute = nameof(CanRecordFeedback))]
    private async Task SaveFeedbackAsync(string verdict)
    {
        if (!CanRecordFeedback || verdict is not ("ok" or "problem")) return;
        string key = feedbackKey!;
        IsBusy = true;
        try
        {
            await executions.RecordFeedbackAsync(key, verdict, time.GetUtcNow().UtcDateTime);
            feedbackRecorded = true;
            FeedbackStatus = Strings.Get(verdict == "ok" ? "Feedback.SavedOk" : "Feedback.SavedProblem");
        }
        catch (Exception ex) { AppLog.Failure("Feedback", ex); FeedbackStatus = Strings.Get("Feedback.Failed"); }
        finally { IsBusy = false; }
    }

    partial void OnSelectedFolderChanged(FolderNode? oldValue, FolderNode? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null)
        {
            void ExpandAncestors(FolderNode node)
            {
                if (!ScanPaths.IsExcluded(newValue.Path, [node.Path])) return;
                if (node != newValue) node.IsExpanded = true;
                foreach (FolderNode child in node.Children) ExpandAncestors(child);
            }
            foreach (FolderNode root in FolderRoots) ExpandAncestors(root);
            newValue.IsSelected = true;
        }
        VisibleFiles = newValue is null ? Files : [.. Files.Where(f => ScanPaths.IsExcluded(f.Path, [newValue.Path]))];
        SelectedMapFolder = MapFolders.FirstOrDefault(f => newValue is not null && ScanPaths.IsExcluded(newValue.Path, [f.Path]));
    }

    partial void OnSelectedMapFolderChanged(FolderNode? value)
    {
        if (value is not null && (SelectedFolder is null || !ScanPaths.IsExcluded(SelectedFolder.Path, [value.Path])))
            SelectedFolder = value;
    }

    partial void OnSelectedFileChanged(FileRow? value)
    {
        if (value is null) return;
        FolderNode? Find(FolderNode node) => node.Children.Select(Find).FirstOrDefault(n => n is not null)
            ?? (ScanPaths.IsExcluded(value.Path, [node.Path]) ? node : null);
        SelectedFolder = FolderRoots.Select(Find).FirstOrDefault(n => n is not null);
    }

    [RelayCommand] private void ShowAllFiles() => SelectedFolder = FolderRoots.FirstOrDefault();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectionSummary))] public partial IReadOnlyList<CleanupRow> Cleanup { get; set; } = [];
    [ObservableProperty] public partial string CleanupStatus { get; set; } = Strings.Get("Cleanup.NotEstimated");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanInstallModel), nameof(CanExplainCleanup))]
    public partial bool IsBusy { get; set; }
    public bool IsIdle => !IsBusy;
    partial void OnIsBusyChanged(bool value)
    {
        CleanCommand.NotifyCanExecuteChanged();
        SaveFeedbackCommand.NotifyCanExecuteChanged();
        UninstallProgramCommand.NotifyCanExecuteChanged();
        ExplainSelectedFileCommand.NotifyCanExecuteChanged();
        ExplainRemnantsCommand.NotifyCanExecuteChanged();
        RemoveRemnantCommand.NotifyCanExecuteChanged();
    }
    [ObservableProperty] public partial string QuarantineStatus { get; set; } = "";
    [ObservableProperty] public partial bool HasLegacyQuarantine { get; set; }
    [ObservableProperty] public partial string AssistantQuestion { get; set; } = Strings.Get("AI.DefaultQuestion");
    [ObservableProperty] public partial string AssistantStatus { get; set; } = Strings.Get("AI.Unavailable");
    [ObservableProperty] public partial string AssistantOutput { get; set; } = "";
    [ObservableProperty] public partial string ModelDownloadStatus { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstallModel), nameof(CanExplainCleanup), nameof(ModelInstallButtonText))]
    public partial bool ModelInstalled { get; set; }
    [ObservableProperty] public partial bool IsModelDownloading { get; set; }
    [ObservableProperty] public partial bool IsAssistantRunning { get; set; }
    public bool ModelRuntimeAvailable => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >= 8L * 1024 * 1024 * 1024;
    public bool CanInstallModel => AssistantEnabled && IsIdle && !ModelInstalled && ModelRuntimeAvailable;
    public bool CanExplainCleanup => AssistantEnabled && IsIdle && ModelInstalled && ModelRuntimeAvailable;
    public string ModelInstallButtonText => Strings.Get(ModelInstalled ? "AI.InstalledButton"
        : ModelRuntimeAvailable ? "AI.DownloadButton" : "AI.MemoryRequirement");
    [ObservableProperty] public partial string RootsSummary { get; set; } = "";


    // ScanCommand.IsRunning drives busy state; ScanCancelCommand is generated alongside it.
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken cancellation)
    {
        FileAdvice = "";
        Files = [];
        SelectedFolder = null;
        VisibleFiles = [];
        FolderRoots = [];
        MapFolders = [];
        AnalysisCoverage = Strings.Get("Analysis.Empty");
        ScanComparison = "";
        RootsSummary = "";
        IsBusy = true;
        Status = Strings.Get("Scan.Running");
        try
        {
            using var progress = new UiProgress<ScanProgress>(p =>
            {
                Status = Strings.Format("Scan.Progress", DisplayFormat.Count(p.Files), DisplayFormat.Size(p.LogicalBytes), DisplayFormat.Count(p.Skipped), DisplayFormat.Count(p.Errors));
                if (p.Roots is not null) RootsSummary = RootsText(p.Roots);
            });
            SaveExclusions();
            string[] excluded = [.. exclusions.Paths];
            var result = await scanner.ScanAsync(new(RootPath.Trim(), excluded), progress, cancellation);
            lastScan = result;
            progress.Dispose();
            Files = result.LargestFiles.Select(FileRow.From).ToArray();
            FolderNode tree = FolderNode.Build(result);
            FolderRoots = [tree];
            MapFolders = tree.Children.Take(64).ToArray();
            SelectedFolder = tree;
            AnalysisCoverage = Strings.Format("Analysis.Coverage", DisplayFormat.Count(Files.Count), DisplayFormat.Count(result.Files))
                + (result.FolderAggregationTruncated ? Strings.Get("Analysis.Truncated") : "")
                + (result.State != ScanState.Completed ? Strings.Get("Analysis.Partial") : "");
            AppLog.Info("Scan finished: state {State}, files {Files}, excluded {Excluded}, inaccessible {Inaccessible}", result.State, result.Files, result.ExcludedByPolicy, result.Inaccessible);
            Status = result.State == ScanState.RootNotFound
                ? Strings.Format("Scan.RootNotFound", result.Root)
                : Strings.Format("Scan.Summary", Display(result.State), DisplayFormat.Count(result.Files), DisplayFormat.Size(result.LogicalBytes));
            Details = Coverage(result) + Strings.Get("Scan.VolumeExplanation")
                + string.Join("\n", result.Diagnostics);
            // The cache is regenerable: a failed save is reported, not fatal.
            try
            {
                var previous = (await history.RecentAsync(5)).FirstOrDefault(s =>
                    string.Equals(s.Root, result.Root, StringComparison.OrdinalIgnoreCase) && s.State == ScanState.Completed);
                if (previous is not null && result.State == ScanState.Completed && excluded.Length == 0)
                    ScanComparison = Strings.Format("Analysis.Comparison", previous.CompletedUtc.ToLocalTime(),
                        DisplayFormat.Size(previous.LogicalBytes), DisplayFormat.Size(result.LogicalBytes),
                        (result.LogicalBytes >= previous.LogicalBytes ? "+" : "−") + DisplayFormat.Size(Math.Abs(result.LogicalBytes - previous.LogicalBytes)))
                        + Strings.Get("Analysis.ComparisonScope");
                await history.SaveAsync(result, DateTime.UtcNow, CancellationToken.None);
                await folderHistory.SaveAsync(result, time.GetUtcNow().UtcDateTime, CancellationToken.None);
                FolderTrendPaths = await folderHistory.PathsAsync();
                SelectedTrendPath = result.Root;
                await LoadFolderTrendAsync(SelectedTrendPath);
                await history.TrimAsync();
            }
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
            SaveExclusions();
            IReadOnlyList<RulePreview> previews = await Task.Run(() => cleanup.Preview(cancellation), cancellation);
            AppLog.Info("Cleanup estimate took {Milliseconds} ms", clock.ElapsedMilliseconds);
            RecycleBinInfo? binInfo = await Task.Run(recycleBin.Query, cancellation);
            CleanupRow[] found = await Task.Run(() => previews.Where(p => p.Items.Count > 0)
                .Concat(binInfo is { Items: > 0 } && exclusions.Paths.Count == 0 ? [SystemRules.RecycleBinPreview(binInfo.Items, binInfo.Bytes)] : [])
                .Select(p => new CleanupRow(p)).OrderByDescending(r => r.LogicalBytes).ToArray(), cancellation);
            SetRows(found);
            AppLog.Info("Cleanup estimate: {Categories} categories, {Files} files", found.Length, found.Sum(r => r.Preview.Items.Count));
            CleanupStatus = found.Length == 0
                ? Strings.Get("Cleanup.NothingFound")
                : Strings.Format("Cleanup.EstimateSummary", DisplayFormat.Size(found.Sum(r => r.LogicalBytes)), DisplayFormat.Count(found.Length))
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

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallModelAsync(CancellationToken cancellation)
    {
        if (IsBusy || !AssistantEnabled || modelInstaller.IsInstalled) return;
        if (!ModelRuntimeAvailable) { AssistantStatus = Strings.Get("AI.LowMemory"); return; }
        string message = Strings.Format("AI.DownloadConfirm", DisplayFormat.Size(modelInstaller.Model.SizeBytes), modelInstaller.Model.License,
                modelInstaller.Model.Id, modelInstaller.Model.LicenseUri)
            + Strings.Get("AI.DownloadPrivacy");
        if (!confirmation.Confirm(Strings.Get("AI.DownloadTitle"), message)) return;

        IsBusy = true;
        IsModelDownloading = true;
        ModelDownloadStatus = Strings.Get("AI.DownloadStarting");
        AssistantStatus = Strings.Get("AI.DownloadStarting");
        try
        {
            using var progress = new UiProgress<ModelDownloadProgress>(p =>
                ModelDownloadStatus = Strings.Format("AI.DownloadProgress", DisplayFormat.Size(p.DownloadedBytes), DisplayFormat.Size(p.TotalBytes)));
            await modelInstaller.InstallAsync(progress, cancellation);
            ModelInstalled = modelInstaller.IsInstalled;
            ModelDownloadStatus = Strings.Get("AI.DownloadComplete");
            AssistantStatus = Strings.Get("AI.Ready");
            AppLog.Info("Local model installation completed: {ModelId}", modelInstaller.Model.Id);
        }
        catch (OperationCanceledException) { ModelDownloadStatus = Strings.Get("AI.DownloadCancelled"); }
        catch (Exception ex)
        {
            AppLog.Failure("Local model installation", ex);
            ModelDownloadStatus = Strings.Get("AI.DownloadFailed") + ex.Message;
        }
        finally { IsModelDownloading = false; IsBusy = false; }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ExplainCleanupAsync(CancellationToken cancellation)
    {
        if (IsBusy || !AssistantEnabled || !modelInstaller.IsInstalled) return;
        if (!ModelRuntimeAvailable) { AssistantStatus = Strings.Get("AI.LowMemory"); return; }
        RulePreview[] previews = [.. Cleanup.Where(r => r.CanSelect).Select(r => r.Preview).Take(32)];
        if (previews.Length == 0) { AssistantStatus = Strings.Get("AI.NoFacts"); return; }
        AssistantStatus = Strings.Get("AI.Analyzing");
        AssistantOutput = "";
        IsBusy = true;
        IsAssistantRunning = true;
        try
        {
            AssistantFact[] facts = [.. previews.Select((p, index) => new AssistantFact($"F{index + 1:000}",
                p.Rule.TitleRu,
                p.Rule.Consequence switch
                {
                    Spacewise.Catalog.Consequence.Regenerated => "regenerated",
                    Spacewise.Catalog.Consequence.Redownloaded => "redownloaded",
                    _ => "permanent",
                },
                p.Rule.SideEffectsRu, p.FileCount, p.LogicalBytes))];
            AssistantResponse response = await assistants.Get(AssistantEnabled ? modelInstaller.Model.Id : "disabled").ExplainAsync(AssistantQuestion, facts, cancellation);
            var byId = facts.ToDictionary(f => f.Id, f => f.Category, StringComparer.Ordinal);
            AssistantOutput = response.SummaryRu + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, response.Suggestions.Select(s => $"{byId[s.FactId]}: {s.ExplanationRu}"));
            AssistantStatus = Strings.Get("AI.AdviceOnly");
        }
        catch (OperationCanceledException) { AssistantStatus = Strings.Get("AI.Cancelled"); }
        catch (Exception ex)
        {
            AppLog.Failure("Local assistant", ex);
            AssistantStatus = Strings.Get("AI.Failed") + ex.Message;
        }
        finally { IsAssistantRunning = false; IsBusy = false; }
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
                + Strings.Format("Cleanup.SelectionEstimate", DisplayFormat.Size(chosen.Sum(r => r.LogicalBytes)));
        }
    }

    [RelayCommand]
    private void SelectAll() { foreach (CleanupRow row in Cleanup.Where(r => r.CanSelect)) row.IsSelected = true; }

    [RelayCommand]
    private void SelectNone() { foreach (CleanupRow row in Cleanup) row.IsSelected = false; }

    private bool CanClean() => IsIdle && Cleanup.Any(r => r.IsSelected && r.CanSelect);

    [RelayCommand(CanExecute = nameof(CanClean), IncludeCancelCommand = true)]
    private async Task CleanAsync(CancellationToken cancellation)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
        SaveExclusions();
        RulePreview[] chosen = [.. Cleanup.Where(r => r.IsSelected && r.CanSelect).Select(r => r.Preview)];
        RulePreview? bin = chosen.FirstOrDefault(p => p.Rule.Action == SystemRules.RecycleBinAction);
        if (bin is not null && exclusions.Paths.Count > 0)
            throw new InvalidOperationException(Strings.Get("Cleanup.ExcludedRecycleBin"));
        CleanupPlan current = await Task.Run(() => PlanBuilder.Build(chosen, time.GetUtcNow().UtcDateTime), cancellation);
        cancellation.ThrowIfCancellationRequested();
        bool hasFiles = current.Items.Count > 0;
        if (hasFiles)
        {
            PolicyDecision decision = await Task.Run(() => MutationPolicy.Evaluate(current, time.GetUtcNow().UtcDateTime), cancellation);
            if (!decision.Allowed) { CleanupStatus = Strings.Get("Cleanup.Rejected") + decision.Reason; return; }
        }
        else if (bin is null) { CleanupStatus = Strings.Get("Cleanup.NoFiles"); return; }

        long expectedBytes = await Task.Run(() => current.ExpectedBytes, cancellation);
        var message = new System.Text.StringBuilder();
        if (hasFiles)
            message.Append(Strings.Format("Cleanup.ConfirmFiles", DisplayFormat.Count(current.Items.Count), DisplayFormat.Size(expectedBytes)))
                .Append(Strings.Get("Cleanup.ConfirmPermanent"))
                .Append(Strings.Get("Cleanup.ConfirmSkipped"));
        if (bin is not null)
            message.Append(Strings.Format("Cleanup.ConfirmBin", DisplayFormat.Count(bin.SystemCount), DisplayFormat.Size(bin.SystemBytes)))
                .Append(Strings.Get("Cleanup.ConfirmBinPermanent"));
        message.Append(Strings.Get("Common.Continue"));
        if (!confirmation.Confirm(Strings.Get("Cleanup.Title"), message.ToString())) { CleanupStatus = Strings.Get("Cleanup.Cancelled"); return; }

        CleanupStatus = Strings.Get("Cleanup.Running");
        CleanupReport = "";
        FeedbackStatus = "";
        feedbackRecorded = false;
        feedbackKey = "system:recycle-bin:" + current.CreatedUtc.ToString("O", CultureInfo.InvariantCulture);
            var lines = new List<string>();
            if (hasFiles)
            {
                SaveExclusions();
                long planId = await plans.SaveAsync(current, cancellation: CancellationToken.None, directDelete: true);
                feedbackKey = "plan:" + planId.ToString(CultureInfo.InvariantCulture);
                string[] volumes = [.. current.Items.Select(i => System.IO.Path.GetPathRoot(i.Path) ?? "")
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                var before = volumes.ToDictionary(v => v, v => space.FreeBytes(v), StringComparer.OrdinalIgnoreCase);
                ExecutionReport report = await Task.Run(() => executor.Execute(current, cancellation, executions.JournalFor(planId, time)), CancellationToken.None);
                var after = volumes.ToDictionary(v => v, v => space.FreeBytes(v), StringComparer.OrdinalIgnoreCase);
                await executions.FinishPlanAsync(planId, report.Cancelled ? "cancelled" : "done", CancellationToken.None);
                AppLog.Info("Cleanup finished: plan {PlanId}, removed {Removed} of {Total}, cancelled {Cancelled}", planId, report.Removed, report.Outcomes.Count, report.Cancelled);
                var skipped = report.Outcomes.Where(o => o.Status != RemoveStatus.Removed).GroupBy(o => o.Status).Select(g => $"{Skipped(g.Key)}: {DisplayFormat.Count(g.Count())}");
                lines.Add(Strings.Format("Cleanup.Deleted", DisplayFormat.Count(report.Removed), DisplayFormat.Size(report.BytesRemoved))
                    + (report.Cancelled ? Strings.Get("Cleanup.StoppedByUser") : "")
                    + (report.Outcomes.Any(o => o.Status != RemoveStatus.Removed) ? Strings.Get("Cleanup.Skipped") + string.Join(", ", skipped) : "") + ".");
                foreach (string volume in volumes)
                {
                    long logicalBytes = report.Outcomes.Where(o => o.Status == RemoveStatus.Removed
                        && string.Equals(System.IO.Path.GetPathRoot(o.Item.Path) ?? "", volume, StringComparison.OrdinalIgnoreCase))
                        .Sum(o => o.Item.ExpectedSize);
                    long? delta = before[volume] is long b && after[volume] is long a ? a - b : null;
                    lines.Add(Strings.Format("Cleanup.Volume", volume) + Measured(new PurgeReport(0, 0, logicalBytes, delta)));
                }
            }
            if (bin is not null && !cancellation.IsCancellationRequested) lines.Add(await EmptyRecycleBinAsync(bin));
            CleanupStatus = string.Join("\n", lines) + Strings.Get("Cleanup.EstimateStale");
            CleanupReport = string.Join("\n\n", lines);
            SetRows([]);
            await RefreshQuarantineAsync();
            await RefreshHistoryAsync();
        }
        catch (OperationCanceledException) { CleanupStatus = Strings.Get("Cleanup.Cancelled"); }
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

    // Older quarantined files remain available until the user explicitly restores or removes them.
    public async Task InitializeAsync()
    {
        try
        {
            ModelInstalled = modelInstaller.IsInstalled;
            AssistantStatus = !AssistantEnabled ? Strings.Get("AI.Disabled") : !ModelRuntimeAvailable ? Strings.Get("AI.LowMemory")
                : ModelInstalled ? Strings.Get("AI.Ready") : Strings.Get("AI.Unavailable");
            ShowWelcome = await executions.GetSettingAsync("onboarded") is null;
            RecoveryReport recovered = await recovery.RecoverAsync();
            int trimmed = await executions.TrimPurgedRecordsAsync(time.GetUtcNow().UtcDateTime);
            if (trimmed > 0) AppLog.Info("Expired purged quarantine metadata trimmed: {Rows}", trimmed);
            await RefreshQuarantineAsync((recovered.NeverMoved + recovered.Recovered + recovered.Unknown > 0
                    ? Strings.Format("Recovery.Summary", DisplayFormat.Count(recovered.NeverMoved), DisplayFormat.Count(recovered.Recovered), DisplayFormat.Count(recovered.Unknown)) : "")
                + (recovered.UnrecordedFiles > 0 ? Strings.Format("Recovery.Unrecorded", DisplayFormat.Count(recovered.UnrecordedFiles)) : ""));
            await RefreshHistoryAsync();
            await RefreshDisksAsync();
            FolderTrendPaths = await folderHistory.PathsAsync();
            SelectedTrendPath = FolderTrendPaths.FirstOrDefault();
        }
        catch (Exception ex) { AppLog.Failure("Startup recovery", ex); QuarantineStatus = Strings.Get("Quarantine.Unavailable") + ex.Message; }
    }

    private async Task RefreshQuarantineAsync(string prefix = "")
    {
        IReadOnlyList<ExecutionStore.QuarantinedFile> held = await quarantine.HeldAsync();
        HasLegacyQuarantine = held.Count > 0 || prefix.Length > 0;
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
                + (unused.WithoutSize > 0 ? Strings.Format("Usage.UnknownSizes", DisplayFormat.Count(unused.WithoutSize)) : "");
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
