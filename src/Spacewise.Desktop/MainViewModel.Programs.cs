using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spacewise.Core;
using Spacewise.Data;
using Spacewise.Inference;
using Spacewise.Platform.Windows;

namespace Spacewise.Desktop;

public sealed record ProgramRemnantRow(FileFinding Finding, PlanItem? Removal)
{
    public string Path => Finding.Path;
    public string Size => DisplayFormat.Size(Finding.LogicalBytes);
    public string Availability => Strings.Get(Removal is null ? "Programs.ReviewOnly" : "Programs.CanRemove");
}

public sealed partial class MainViewModel
{
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UninstallProgramCommand))]
    public partial UsageRow? SelectedProgram { get; set; }

    [ObservableProperty] public partial string ProgramActionStatus { get; set; } = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ExplainRemnantsCommand))]
    public partial IReadOnlyList<ProgramRemnantRow> ProgramRemnants { get; set; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RemoveRemnantCommand))]
    public partial ProgramRemnantRow? SelectedRemnant { get; set; }
    [ObservableProperty] public partial bool IsSearchingRemnants { get; set; }
    [ObservableProperty] public partial string RemnantAdvice { get; set; } = "";
    private InstalledProgram? removedProgram;

    private bool CanUninstallProgram() => IsIdle && SelectedProgram?.Usage.Program is
        { Kind: ProgramKind.Win32 } program && (program.MsiProductCode is not null || program.UninstallString is not null);

    [RelayCommand(CanExecute = nameof(CanUninstallProgram), IncludeCancelCommand = true)]
    private async Task UninstallProgramAsync(CancellationToken cancellation)
    {
        if (!CanUninstallProgram()) return;
        InstalledProgram selected = SelectedProgram!.Usage.Program;
        IsBusy = true;
        try
        {
            InstalledProgram? registered = await Task.Run(() => ProgramInventory.ReadRegistration(selected.Id));
            if (registered is null || registered.Name != selected.Name)
            {
                ProgramActionStatus = Strings.Get("Programs.Changed");
                return;
            }
            if (await Task.Run(() => ProgramUninstaller.Prepare(registered)) is null)
            {
                ProgramActionStatus = Strings.Get("Programs.Unsupported");
                return;
            }
            if (!confirmation.Confirm(Strings.Get("Programs.Uninstall"), Strings.Format("Programs.Confirm", registered.Name))) return;

            SaveExclusions();
            ProgramRemnants = [];
            removedProgram = null;
            RemnantAdvice = "";
            ProgramActionStatus = Strings.Get("Programs.Uninstalling");
            int? code = await Task.Run(() => ProgramUninstaller.RunAsync(registered));
            if (code is null) { ProgramActionStatus = Strings.Get("Programs.Changed"); return; }
            await LoadUsageAsync();
            if (code is 1641 or 3010) { ProgramActionStatus = Strings.Get("Programs.Restart"); return; }
            if (code != 0) { ProgramActionStatus = Strings.Format("Programs.ExitCode", code); return; }
            if (await Task.Run(() => ProgramInventory.RegistrationExists(registered.Id)))
            {
                ProgramActionStatus = Strings.Get("Programs.StillRegistered");
                return;
            }

            ProgramActionStatus = Strings.Get("Programs.Searching");
            IsSearchingRemnants = true;
            IReadOnlyList<RemnantLocation> locations = await Task.Run(() =>
                ProgramUninstaller.RemainingLocations(registered, ProgramInventory.Read(requireComplete: true), userFolders), cancellation);
            if (locations.Count == 0)
            {
                ProgramActionStatus = Strings.Get("Programs.NoLocation");
                return;
            }
            var results = new List<ScanResult>();
            var metadata = new MetadataScanner();
            foreach (RemnantLocation location in locations.Where(l => !locations.Any(other => other != l && ProtectedPaths.IsUnder(l.Path, other.Path))))
            {
                cancellation.ThrowIfCancellationRequested();
                results.Add(await metadata.ScanAsync(new(location.Path, exclusions.Paths, MaximumFiles: 50_000), null, cancellation));
            }
            cancellation.ThrowIfCancellationRequested();
            ProgramRemnants = [.. results.SelectMany(r => r.LargestFiles).OrderByDescending(f => f.LogicalBytes).Take(100)
                .Select(file => new ProgramRemnantRow(file, PlanRemnant(file, locations)))];
            removedProgram = registered;
            ProgramActionStatus = Strings.Format("Programs.Remnants", DisplayFormat.Count(results.Sum(r => r.Files)), DisplayFormat.Count(ProgramRemnants.Count))
                + (results.Any(r => r.State is not (ScanState.Completed or ScanState.RootNotFound) || r.ExcludedByPolicy > 0)
                    ? Strings.Get("Programs.Incomplete") : "");
        }
        catch (OperationCanceledException) { ProgramActionStatus = Strings.Get("Programs.SearchCancelled"); }
        catch (Exception ex)
        {
            AppLog.Failure("Program uninstall or leftover scan", ex);
            ProgramActionStatus = Strings.Get("Programs.Failed") + ex.Message;
        }
        finally { IsSearchingRemnants = false; IsBusy = false; }
    }

    private PlanItem? PlanRemnant(FileFinding file, IReadOnlyList<RemnantLocation> locations)
    {
        return file is { Identity: { } identity, LastWriteUtc: { } modified, Links: 1 }
            && locations.Any(l => l.CanOfferRemoval && ProtectedPaths.IsUnder(file.Path, l.Path))
            && cleanupPathPolicy.Check(file.Path).Allowed
            ? new("program.remnant", 1, "delete", file.Path, file.LogicalBytes, modified, identity) : null;
    }

    private bool CanRemoveRemnant() => IsIdle && removedProgram is not null && SelectedRemnant is { Removal: not null };

    [RelayCommand(CanExecute = nameof(CanRemoveRemnant))]
    private async Task RemoveRemnantAsync()
    {
        if (!CanRemoveRemnant()) return;
        InstalledProgram program = removedProgram!;
        ProgramRemnantRow row = SelectedRemnant!;
        PlanItem item = row.Removal!;
        CleanupPlan plan = CleanupPlan.Create([item], time.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(5), "program.remnant");
        if (!confirmation.Confirm(Strings.Get("Programs.RemoveFile"), Strings.Format("Programs.ConfirmRemnant", item.Path))) return;

        IsBusy = true;
        try
        {
            SaveExclusions();
            long planId = await plans.SaveAsync(plan, directDelete: true);
            long? before = space.FreeBytes(item.Path);
            ExecutionReport? result = await Task.Run(() =>
                !ProgramInventory.RegistrationExists(program.Id)
                && ProgramUninstaller.RemainingLocations(program, ProgramInventory.Read(requireComplete: true), userFolders)
                    .Any(l => l.CanOfferRemoval && ProtectedPaths.IsUnder(item.Path, l.Path))
                ? executor.Execute(plan, journal: executions.JournalFor(planId, time)) : null);
            long? after = space.FreeBytes(item.Path);
            await executions.FinishPlanAsync(planId, result is null ? "cancelled" : "done");
            if (result is null) { ProgramActionStatus = Strings.Get("Programs.OwnershipChanged"); return; }
            ItemOutcome outcome = result.Outcomes.Single();
            if (outcome.Status is RemoveStatus.Removed or RemoveStatus.Gone)
                ProgramRemnants = [.. ProgramRemnants.Where(r => r != row)];
            RemnantAdvice = "";
            ProgramActionStatus = outcome.Status == RemoveStatus.Removed
                ? Strings.Format("Cleanup.Deleted", "1", DisplayFormat.Size(item.ExpectedSize)) + ". "
                    + Measured(new PurgeReport(1, 0, item.ExpectedSize, after - before))
                : Strings.Get("Cleanup.Skipped") + Skipped(outcome.Status);
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            AppLog.Failure("Remove program remnant", ex);
            ProgramActionStatus = Strings.Get("Programs.Failed") + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private bool CanExplainRemnants() => IsIdle && ProgramRemnants.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExplainRemnants), IncludeCancelCommand = true)]
    private async Task ExplainRemnantsAsync(CancellationToken cancellation)
    {
        if (!CanExplainRemnants()) return;
        await ExplainFilesAsync([.. ProgramRemnants.Take(32).Select(row => row.Finding)], "Programs.AssistantQuestion", text => RemnantAdvice = text, cancellation);
    }

    [ObservableProperty] public partial string FileAdvice { get; set; } = "";

    private bool CanExplainSelectedFile() => IsIdle && SelectedFile is not null;

    [RelayCommand(CanExecute = nameof(CanExplainSelectedFile), IncludeCancelCommand = true)]
    private async Task ExplainSelectedFileAsync(CancellationToken cancellation)
    {
        if (IsBusy || SelectedFile is null) return;
        FileFinding? file = lastScan?.LargestFiles.FirstOrDefault(f => f.Path == SelectedFile.Path);
        if (file is null) return;
        await ExplainFilesAsync([file], "Analysis.AssistantQuestion", text => FileAdvice = text, cancellation);
    }

    private async Task ExplainFilesAsync(FileFinding[] files, string question, Action<string> show, CancellationToken cancellation)
    {
        if (!CanExplainCleanup) { show(Strings.Get("Programs.EnableAssistant")); return; }
        AssistantFact[] facts = [.. files.Select((file, index) => new AssistantFact($"F{index + 1:000}",
            Strings.Format("Programs.Fact", index + 1), "unknown", RemnantFactNote(file.Path), 1, file.LogicalBytes))];
        IsBusy = true;
        show(Strings.Get("AI.Analyzing"));
        try
        {
            AssistantResponse response = await assistants.Get(modelInstaller.Model.Id)
                .ExplainAsync(Strings.Get(question), facts, cancellation);
            var paths = facts.Select((fact, index) => (fact.Id, files[index].Path)).ToDictionary(row => row.Id, row => row.Path);
            show(response.Suggestions.Count == 0 ? Strings.Get("Programs.NoAdvice")
                : string.Join(Environment.NewLine, response.Suggestions.Select(s => $"{paths[s.FactId]}\n{s.ExplanationRu}")));
        }
        catch (OperationCanceledException) { show(Strings.Get("AI.Cancelled")); }
        catch (Exception ex)
        {
            AppLog.Failure("Local assistant for file findings", ex);
            show(Strings.Get("AI.Failed") + ex.Message);
        }
        finally { IsBusy = false; }
    }

    private static string RemnantFactNote(string path) => Strings.Get(System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ini" or ".cfg" or ".conf" or ".json" or ".xml" => "Programs.FactSettings",
        ".db" or ".sqlite" or ".sqlite3" or ".bak" => "Programs.FactData",
        ".log" or ".dmp" => "Programs.FactLog",
        _ => "Programs.FactNote",
    });
}
