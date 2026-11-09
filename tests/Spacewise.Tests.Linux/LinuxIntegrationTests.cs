using System.Runtime.InteropServices;
using Spacewise.Core;
using Spacewise.Platform.Linux;

namespace Spacewise.Tests.Linux;

// Run on a real Linux filesystem, as an unprivileged user. Never touches existing user data.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxIntegrationTests : IDisposable
{
    private readonly string root;
    private readonly string cache;
    private readonly string data;
    private readonly LinuxCleanupPolicy policy;
    private readonly LinuxQuarantine quarantine;

    public LinuxIntegrationTests()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Run these integration tests on Linux.");
        root = Path.Combine(Path.GetTempPath(), "spacewise-tests-" + Guid.NewGuid().ToString("N"));
        cache = Path.Combine(root, "cache"); data = Path.Combine(root, "data");
        Directory.CreateDirectory(cache);
        policy = new([cache], data);
        quarantine = new(data, policy);
    }

    private string FileAt(string name, string text = "cache contents")
    {
        string path = Path.Combine(cache, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60));
        return path;
    }

    private static async Task<PlanItem> Item(string path)
    {
        ScanResult scan = await new LinuxScanner().ScanAsync(new(Path.GetDirectoryName(path)!, []), null, TestContext.Current.CancellationToken);
        FileFinding finding = scan.LargestFiles.Single(f => f.Path == path);
        return new("linux.test", 1, "delete", path, finding.LogicalBytes, File.GetLastWriteTimeUtc(path), finding.Identity);
    }

    [Fact]
    public async Task Scan_reports_allocated_bytes_and_keeps_case_distinct()
    {
        FileAt("A/a", "one"); FileAt("a/b", "four");
        ScanResult result = await new LinuxScanner().ScanAsync(new(cache, []), null, TestContext.Current.CancellationToken);
        Assert.Equal(ScanState.Completed, result.State);
        Assert.Equal(2, result.Files); Assert.Equal(7, result.LogicalBytes);
        Assert.Equal(2, result.Folders.Count);
        Assert.All(result.LargestFiles, f => { Assert.NotNull(f.Identity); Assert.NotNull(f.AllocatedBytes); });
    }

    [Fact]
    public async Task Scanner_never_follows_leaf_or_ancestor_symlinks()
    {
        string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret"), "private");
        Directory.CreateSymbolicLink(Path.Combine(cache, "link"), outside);
        File.CreateSymbolicLink(Path.Combine(cache, "file-link"), Path.Combine(outside, "secret"));
        ScanResult result = await new LinuxScanner().ScanAsync(new(cache, []), null, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.Files); Assert.Equal(2, result.Skipped);
        ScanResult throughAncestor = await new LinuxScanner().ScanAsync(new(Path.Combine(cache, "link"), []), null, TestContext.Current.CancellationToken);
        Assert.Equal(0, throughAncestor.Files); Assert.True(throughAncestor.Errors > 0);
    }

    [Fact]
    public async Task Exclusions_are_case_sensitive_and_respect_boundaries()
    {
        FileAt("Keep/file"); FileAt("keep/file"); FileAt("Keep2/file");
        ScanResult result = await new LinuxScanner().ScanAsync(new(cache, [Path.Combine(cache, "Keep")]), null, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Files);
        Assert.DoesNotContain(result.LargestFiles, f => f.Path.Contains("/Keep/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_and_limits_report_incomplete_coverage()
    {
        FileAt("one"); FileAt("two");
        ScanResult limited = await new LinuxScanner().ScanAsync(new(cache, [], MaximumFiles: 1), null, TestContext.Current.CancellationToken);
        Assert.Equal(ScanState.Partial, limited.State); Assert.True(limited.StoppedEarly);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        ScanResult result = await new LinuxScanner().ScanAsync(new(cache, []), null, cancelled.Token);
        Assert.Equal(ScanState.Cancelled, result.State);
    }

    [Fact]
    public async Task Real_plan_quarantines_then_restores_with_manifest_after_restart()
    {
        string file = FileAt("one");
        CleanupPlan plan = CleanupPlan.Create([await Item(file)], DateTime.UtcNow, TimeSpan.FromMinutes(5), "test");
        ExecutionReport report = new PlanExecutor(quarantine, policy, TimeProvider.System).Execute(plan, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Removed); Assert.False(File.Exists(file));
        var reopened = new LinuxQuarantine(data, policy);
        LinuxHeldFile held = Assert.Single(reopened.List());
        Assert.True(reopened.Restore(held.Id).Restored);
        Assert.Equal("cache contents", File.ReadAllText(file)); Assert.Empty(reopened.List());
    }

    [Fact]
    public async Task Changed_file_and_replaced_inode_are_not_moved()
    {
        string file = FileAt("one"); PlanItem original = await Item(file);
        File.WriteAllText(file, "changed");
        Assert.Equal(RemoveStatus.Changed, quarantine.Remove(original).Status);
        File.Delete(file); File.WriteAllText(file, "cache contents"); File.SetLastWriteTimeUtc(file, original.ExpectedMtimeUtc);
        // Keep the old inode allocated during replacement to avoid inode reuse in this test.
        PlanItem current = await Item(file);
        File.Move(file, Path.Combine(cache, "old"));
        File.WriteAllText(file, "cache contents"); File.SetLastWriteTimeUtc(file, current.ExpectedMtimeUtc);
        Assert.Equal(RemoveStatus.Changed, quarantine.Remove(current).Status);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Execution_rechecks_whitelist_and_symlink_parents()
    {
        string file = FileAt("dir/one"); PlanItem item = await Item(file);
        var blocked = new LinuxCleanupPolicy([cache], data, [Path.Combine(cache, "dir")]);
        var plan = CleanupPlan.Create([item], DateTime.UtcNow, TimeSpan.FromMinutes(5), "test");
        Assert.Equal(RemoveStatus.Unsafe, Assert.Single(new PlanExecutor(quarantine, blocked, TimeProvider.System).Execute(plan, TestContext.Current.CancellationToken).Outcomes).Status);
        Directory.Move(Path.Combine(cache, "dir"), Path.Combine(cache, "original"));
        Directory.CreateSymbolicLink(Path.Combine(cache, "dir"), Path.Combine(cache, "original"));
        Assert.NotEqual(RemoveStatus.Removed, quarantine.Remove(item).Status);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Restore_never_overwrites_and_tampered_payload_is_never_purged()
    {
        string file = FileAt("one");
        Assert.Equal(RemoveStatus.Removed, quarantine.Remove(await Item(file)).Status);
        LinuxHeldFile held = Assert.Single(quarantine.List());
        File.WriteAllText(file, "new user content");
        Assert.False(quarantine.Restore(held.Id).Restored);
        Assert.Equal("new user content", File.ReadAllText(file));
        string stored = Path.Combine(data, "quarantine", held.Id, "payload");
        File.WriteAllText(stored, "cache contentX"); // same length; hash verification must catch it.
        Assert.True(quarantine.Restore(held.Id).HashMismatch);
        Assert.False(quarantine.Purge(held.Id)); Assert.True(File.Exists(stored));
    }

    [Fact]
    public async Task Purge_removes_only_held_payload_and_keeps_manifest()
    {
        string file = FileAt("one");
        Assert.Equal(RemoveStatus.Removed, quarantine.Remove(await Item(file)).Status);
        LinuxHeldFile held = Assert.Single(quarantine.List());
        Assert.True(quarantine.Purge(held.Id)); Assert.Empty(quarantine.List());
        Assert.True(File.Exists(Path.Combine(data, "quarantine", held.Id, "manifest.json")));
    }

    [DllImport("libc", SetLastError = true)] private static extern int link(string from, string to);

    [Fact]
    public async Task Hardlinks_and_non_private_data_directories_are_rejected()
    {
        string file = FileAt("one"); Assert.Equal(0, link(file, Path.Combine(cache, "two")));
        Assert.Equal(RemoveStatus.Unsafe, quarantine.Remove(await Item(file)).Status);
        string other = FileAt("other"); Directory.CreateDirectory(data);
        File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        Assert.NotEqual(RemoveStatus.Removed, quarantine.Remove(await Item(other)).Status);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task Catalog_selects_only_old_known_cache_and_honors_whitelist()
    {
        FileAt("thumbnails/old"); string recent = FileAt("thumbnails/recent"); File.SetLastWriteTimeUtc(recent, DateTime.UtcNow);
        FileAt("unknown/old"); FileAt("thumbnails/keep/old");
        var previewer = new LinuxCleanupPreviewer(new(root, cache, data), TimeProvider.System);
        var preview = Assert.Single(await previewer.PreviewAsync([Path.Combine(cache, "thumbnails", "keep")], TestContext.Current.CancellationToken));
        Assert.True(preview.Complete); Assert.Single(preview.Items);
        Assert.EndsWith("/thumbnails/old", preview.Items[0].Path);
    }

    [Fact]
    public void Settings_history_and_permissions_survive_restart()
    {
        var store = new LinuxAppStore(data);
        store.SaveSettings(new([Path.Combine(cache, "Keep")]));
        Assert.Equal(Path.Combine(cache, "Keep"), Assert.Single(new LinuxAppStore(data).Settings().Exclusions));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(data));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(data, "settings.json")));
    }

    [Fact]
    public void Folder_history_keeps_latest_complete_daily_snapshot()
    {
        var store = new LinuxAppStore(data);
        ScanResult Scan(long bytes, ScanState state = ScanState.Completed) => new(Guid.NewGuid(), cache, state, 1, bytes, 0, 0, [], []);
        store.SaveScan(Scan(100)); store.SaveScan(Scan(20)); store.SaveScan(Scan(999, ScanState.Partial));
        var snapshot = Assert.Single(new LinuxAppStore(data).FolderHistory());
        Assert.Equal(20, Assert.Single(snapshot.Folders).LogicalBytes);
        Assert.Equal(3, store.History().Count);
    }

    [Fact]
    public async Task Real_duplicate_analysis_ignores_symlinks_hardlinks_and_exclusions()
    {
        string first = FileAt("a", "equal"), second = FileAt("b", "equal");
        FileAt("different", "other"); FileAt("excluded", "equal");
        File.CreateSymbolicLink(Path.Combine(cache, "symlink"), first);
        string linked = FileAt("linked", "equal"); Assert.Equal(0, link(linked, Path.Combine(cache, "hardlink")));
        var result = await new DuplicateFinder(new LinuxDuplicateSource()).FindAsync(cache, [Path.Combine(cache, "excluded")], TestContext.Current.CancellationToken);
        Assert.Equal([first, second], Assert.Single(result.Groups).Files.Select(f => f.Path).Order().ToArray());
        Assert.Equal(3, result.Candidates);
    }

    [Fact]
    public async Task Inaccessible_directory_is_reported_and_never_selectable()
    {
        string file = FileAt("thumbnails/locked/item");
        string locked = Path.GetDirectoryName(file)!;
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            ScanResult result = await new LinuxScanner().ScanAsync(new(cache, []), null, TestContext.Current.CancellationToken);
            Assert.Equal(ScanState.Partial, result.State); Assert.True(result.Errors > 0);
            var category = Assert.Single(await new LinuxCleanupPreviewer(new(root, cache, data), TimeProvider.System)
                .PreviewAsync([], TestContext.Current.CancellationToken));
            Assert.False(category.Complete);
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public async Task User_catalog_glob_is_applied_and_traversal_is_rejected()
    {
        FileAt("mytool/item.tmp"); FileAt("mytool/item.txt");
        string rules = Path.Combine(data, "rules"); Directory.CreateDirectory(rules);
        string file = Path.Combine(rules, "custom.json");
        File.WriteAllText(file, """{"version":1,"rules":[{"id":"custom.tool","title":"Tool","directory":"mytool","minimumAgeDays":1,"include":["**/*.tmp"],"consequences":"Review files"}]}""");
        var result = Assert.Single(await new LinuxCleanupPreviewer(new(root, cache, data), TimeProvider.System).PreviewAsync([], TestContext.Current.CancellationToken));
        Assert.True(result.Rule.UserDefined); Assert.Single(result.Items); Assert.EndsWith("item.tmp", result.Items[0].Path);
        File.WriteAllText(file, """{"version":1,"rules":[{"id":"custom.tool","title":"Tool","directory":"../mytool","minimumAgeDays":1,"consequences":"Review files"}]}""");
        Assert.Throws<InvalidDataException>(() => LinuxCatalog.Load(rules));
    }

    public void Dispose()
    {
        // Only the fresh, unique fixture root owned by this test; never recurse through symlinks.
        if (!root.StartsWith(Path.Combine(Path.GetTempPath(), "spacewise-tests-"), StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture root.");
        Directory.Delete(root, recursive: true);
    }
}
