using Spacewise.Core;

string fixture = Path.Combine(Path.GetTempPath(), "SpacewiseSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
int count = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); count++; }
try
{
    Directory.CreateDirectory(Path.Combine(fixture, "excluded"));
    File.WriteAllBytes(Path.Combine(fixture, "keep.bin"), new byte[17]);
    File.WriteAllBytes(Path.Combine(fixture, "excluded", "other.bin"), new byte[41]);
    var scanner = new MetadataScanner();
    if (args.Contains("--regression"))
    {
        int failures = 0;
        void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        var omitted = await scanner.ScanAsync(new(fixture, [Path.Combine(fixture, "excluded")]), null, default);
        Check(omitted.State == ScanState.Completed, "intentional exclusion should complete selected scope");
        var regressionMissing = await scanner.ScanAsync(new(Path.Combine(fixture, "missing"), []), null, default);
        Check(regressionMissing.State == ScanState.Failed && !regressionMissing.Diagnostics.Any(x => x.Contains("Корень и его родители")), "missing root should report not found, not a link");
        var caseExcluded = await scanner.ScanAsync(new(fixture, [Path.Combine(fixture, "excluded").ToUpperInvariant()]), null, default);
        Check(caseExcluded.Files == 1, "Windows exclusion accepts alternate casing");
        var trailing = await scanner.ScanAsync(new(fixture, [fixture + Path.DirectorySeparatorChar]), null, default);
        Check(trailing.Files == 0 && trailing.Skipped == 1, "trailing separator excludes root as one subtree");
        var driveRoot = await scanner.ScanAsync(new(fixture, [Path.GetPathRoot(fixture)!]), null, default);
        Check(driveRoot.Files == 0, "drive root exclusion covers descendants");
        var wide = new List<string>();
        try
        {
            for (int i = 0; i < 10_002; i++) { string p = Path.Combine(fixture, "wide-" + i); Directory.CreateDirectory(p); wide.Add(p); }
            var overflowed = await scanner.ScanAsync(new(fixture, []), null, default);
            Check(overflowed.State == ScanState.Completed && overflowed.Skipped == 0 && overflowed.Files == 2,
                "wide directory with 10002 children is fully traversed");
        }
        finally { foreach (string p in wide) Directory.Delete(p); }
        var regressionExact = await scanner.ScanAsync(new(fixture, [], 2), null, default);
        Check(regressionExact.State == ScanState.Completed, "exact file limit is complete when nothing remains");
        Console.WriteLine($"Regression failures: {failures}");
        Environment.ExitCode = failures == 0 ? 0 : 1;
        return;
    }
    var all = await scanner.ScanAsync(new(fixture, []), null, default);
    Assert(all.Files == 2 && all.LogicalBytes == 58 && all.State == ScanState.Completed, "metadata totals");
    var filtered = await scanner.ScanAsync(new(fixture, [Path.Combine(fixture, "excluded")]), null, default);
    Assert(filtered.Files == 1 && filtered.LogicalBytes == 17 && filtered.SkippedByPolicy == 1 && filtered.State == ScanState.Completed, "exclusion completes selected scope");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var stopped = await scanner.ScanAsync(new(fixture, []), null, cancelled.Token);
    Assert(stopped.State == ScanState.Cancelled && stopped.Files == 0, "pre-cancelled scan");
    var limited = await scanner.ScanAsync(new(fixture, [], 1), null, default);
    Assert(limited.State == ScanState.Partial && limited.Files == 1, "bounded scan");
    Assert(all.LargestFiles.All(x => x.AllocatedBytes == null), "physical allocation is unknown");
    Assert(!ReadOnlyPolicy.EvaluateMutation().Allowed && !new DisabledLocalAssistant().IsAvailable, "mutations denied and model disabled");
    var session = new ScanSession(); session.Start();
    bool rejected = false; try { session.Start(); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "parallel scan rejected"); session.Finish(ScanState.Completed);
    Assert(File.ReadAllBytes(Path.Combine(fixture, "keep.bin")).Length == 17 && Directory.GetFiles(fixture, "*", SearchOption.AllDirectories).Length == 2, "scan preserved fixture files");
    Assert(new[] { FileAttributes.ReparsePoint, FileAttributes.Offline, (FileAttributes)0x40000, (FileAttributes)0x400000 }
        .All(MetadataScanPolicy.ShouldSkip), "each link/offline/cloud-recall flag is independently excluded");
    Assert(!MetadataScanPolicy.ShouldSkip(FileAttributes.Archive | FileAttributes.ReadOnly), "ordinary archive/read-only metadata remains eligible");
    Assert(MetadataScanPolicy.IsNetwork(DriveType.Network) && !MetadataScanPolicy.IsNetwork(DriveType.Fixed), "mapped-network drive classification rejected");
    FileAttributes initial = File.GetAttributes(fixture);
    try
    {
        File.SetAttributes(fixture, initial | FileAttributes.Offline);
        Assert((File.GetAttributes(fixture) & FileAttributes.Offline) != 0, "offline root fixture flag applied");
        var offlineRoot = await scanner.ScanAsync(new(fixture, []), null, default);
        Assert(offlineRoot.State == ScanState.Failed && offlineRoot.Files == 0, "offline root is rejected before enumeration");
    }
    finally { File.SetAttributes(fixture, initial); }
    var exact = await scanner.ScanAsync(new(fixture, [], 2), null, default);
    Assert(exact.State == ScanState.Completed && exact.Files == 2, "exact file limit is complete");
    var missing = await scanner.ScanAsync(new(Path.Combine(fixture, "missing"), []), null, default);
    Assert(missing.State == ScanState.Failed && missing.Errors == 1 && !missing.Diagnostics.Any(x => x.Contains("Корень и его родители")), "missing root reports not found");
    var alternateCase = await scanner.ScanAsync(new(fixture, [Path.Combine(fixture, "excluded").ToUpperInvariant()]), null, default);
    Assert(alternateCase.Files == 1 && alternateCase.State == ScanState.Completed, "case-insensitive exclusion policy");
    var rootExcluded = await scanner.ScanAsync(new(fixture, [fixture + Path.DirectorySeparatorChar]), null, default);
    Assert(rootExcluded.Files == 0 && rootExcluded.SkippedByPolicy == 1 && rootExcluded.State == ScanState.Completed, "root exclusion with trailing separator");
    var driveExcluded = await scanner.ScanAsync(new(fixture, [Path.GetPathRoot(fixture)!]), null, default);
    Assert(driveExcluded.Files == 0 && driveExcluded.SkippedByPolicy == 1, "volume-root exclusion");
    Assert(!ScanPaths.IsExcluded(fixture + "-sibling", [ScanPaths.Normalize(fixture)]), "exclusion respects path boundary");
    var quoted = await scanner.ScanAsync(new('"' + fixture + '"', []), null, default);
    Assert(quoted.State == ScanState.Completed && quoted.LogicalBytes == 58, "Copy as path quotes accepted");
    string level1 = Path.Combine(fixture, "excluded", "level1");
    string level2 = Path.Combine(level1, "level2");
    Directory.CreateDirectory(level2);
    try
    {
        var depthLimited = await scanner.ScanAsync(new(fixture, [], MaximumDirectoryDepth: 2), null, default);
        Assert(depthLimited.State == ScanState.Partial && depthLimited.UnvisitedDirectories == 1
            && depthLimited.Diagnostics.Any(x => x.Contains("Лимит глубины")), "depth limit reports omitted subtree");
        var nested = await scanner.ScanAsync(new(fixture, []), null, default);
        Assert(nested.State == ScanState.Completed && nested.UnvisitedDirectories == 0, "ordinary deep traversal resumes ancestors");
    }
    finally { Directory.Delete(level2); Directory.Delete(level1); }
    Console.WriteLine($"{count} checks passed");
}
finally
{
    // Only the uniquely created fixture paths above; no arbitrary recursive deletion.
    File.Delete(Path.Combine(fixture, "keep.bin"));
    File.Delete(Path.Combine(fixture, "excluded", "other.bin"));
    Directory.Delete(Path.Combine(fixture, "excluded"));
    Directory.Delete(fixture);
}
