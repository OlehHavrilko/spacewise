using System.Runtime.InteropServices;
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
        Check(regressionMissing.State == ScanState.RootNotFound && !regressionMissing.Diagnostics.Any(x => x.Contains("Корень и его родители")), "missing root should report not found, not a link");
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
    Assert(stopped.State == ScanState.Cancelled && stopped.Files == 0 && stopped.StoppedEarly, "pre-cancelled scan");
    var limited = await scanner.ScanAsync(new(fixture, [], 1), null, default);
    Assert(limited.State == ScanState.Partial && limited.Files == 1 && limited.StoppedEarly, "bounded scan");
    Assert(!all.StoppedEarly && all.ExcludedByPolicy == 0 && all.Inaccessible == 0, "complete scan has empty coverage buckets");
    Assert(all.LargestFiles.All(x => x.AllocatedBytes >= 0 && x.Links == 1 && x.Identity != null),
        "top-N files carry allocated size, link count and file ID");
    Assert(all.LargestFiles.Select(x => x.Identity).Distinct().Count() == 2, "distinct files have distinct IDs");
    string link = Path.Combine(fixture, "link.bin");
    Assert(CreateHardLinkW(link, Path.Combine(fixture, "keep.bin"), IntPtr.Zero), "hard link fixture created");
    try
    {
        var linked = await scanner.ScanAsync(new(fixture, []), null, default);
        var pair = linked.LargestFiles.Where(x => x.LogicalBytes == 17).ToArray();
        Assert(pair.Length == 2 && pair.All(x => x.IsHardLinked && x.Links == 2) && pair[0].Identity == pair[1].Identity,
            "hard link names share one ID and are marked");
    }
    finally { File.Delete(link); }
    Assert(DisplayFormat.Size(17) == "17\u00A0байт" && DisplayFormat.Size(1023) == "1\u202F023\u00A0байт", "bytes below 1 KB");
    Assert(DisplayFormat.Size(5_153_960_755) == "4,8\u00A0ГБ" && DisplayFormat.Size(214L << 30) == "214\u00A0ГБ"
        && DisplayFormat.Size(1024) == "1,0\u00A0КБ", "binary units, decimal comma, integers from 100");
    Assert(DisplayFormat.Size(1_048_575) == "1,0\u00A0МБ" && DisplayFormat.Size(102_348) == "99,9\u00A0КБ"
        && DisplayFormat.Size(102_349) == "100\u00A0КБ", "unit promotion and 100 boundary");
    Assert(DisplayFormat.Count(12_840) == "12\u202F840" && DisplayFormat.Count(0) == "0", "narrow-space thousands");
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
    Assert(missing.State == ScanState.RootNotFound && missing.Errors == 0 && missing.Diagnostics.Any(x => x.Contains("не найдена"))
        && !missing.Diagnostics.Any(x => x.Contains("Корень и его родители")), "missing root reports RootNotFound");
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

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
