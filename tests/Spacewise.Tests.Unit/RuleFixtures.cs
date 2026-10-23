using Spacewise.Catalog;
using Spacewise.Core;

namespace Spacewise.Tests.Unit;

// FR-CLN-012: for each fixture tree the rule selects exactly the expected files (marked Pick).
public sealed record FixtureFile(string Path, int AgeDays = 30, FileAttributes Attributes = FileAttributes.Normal, bool Pick = false);

public static class RuleFixtures
{
    private const string L = @"C:\Users\u\AppData\Local", R = @"C:\Users\u\AppData\Roaming", T = L + @"\Temp", W = @"C:\Windows";

    private static FixtureFile Yes(string path, int age = 30) => new(path, age, Pick: true);
    private static FixtureFile No(string path, int age = 30, FileAttributes attributes = FileAttributes.Normal) => new(path, age, attributes);

    private static FixtureFile[] Chromium(string userData) =>
    [
        Yes(userData + @"\Default\Cache\Cache_Data\f_000001"),
        Yes(userData + @"\Default\Cache\Cache_Data\index"),
        Yes(userData + @"\Default\GPUCache\data_0"),
        Yes(userData + @"\Profile 2\Code Cache\js\index-dir\the-real-index"),
        No(userData + @"\Default\Bookmarks"),
        No(userData + @"\Default\Login Data"),
        No(userData + @"\Default\Cache\other"),
        No(userData + @"\System Profile\Cache\Cache_Data\x"),
        No(userData + @"\Local State"),
    ];

    public static readonly IReadOnlyDictionary<string, (string Rule, FixtureFile[] Files)> All = new Dictionary<string, (string, FixtureFile[])>
    {
        ["user-temp"] = ("windows.user.temp",
        [
            Yes(T + @"\a.tmp"), Yes(T + @"\sub\b.log"), No(T + @"\fresh.tmp", age: 0),
            No(T + @"\cloud.txt", attributes: FileAttributes.Offline),
            No(L + @"\Other\c.tmp"),
        ]),
        ["system-temp"] = ("windows.system.temp", [Yes(W + @"\Temp\x.tmp"), No(W + @"\Temp\new.tmp", age: 2), No(W + @"\System32\y.dll")]),
        ["chromium-profile"] = ("browser.chromium.chrome.cache", Chromium(L + @"\Google\Chrome\User Data")),
        ["edge-profile"] = ("browser.chromium.edge.cache", Chromium(L + @"\Microsoft\Edge\User Data")),
        ["brave-profile"] = ("browser.chromium.brave.cache", Chromium(L + @"\BraveSoftware\Brave-Browser\User Data")),
        ["firefox-profile"] = ("browser.firefox.cache",
        [
            Yes(L + @"\Mozilla\Firefox\Profiles\abc.default-release\cache2\entries\E1"),
            Yes(L + @"\Mozilla\Firefox\Profiles\abc.default-release\startupCache\startupCache.8.little"),
            No(L + @"\Mozilla\Firefox\Profiles\abc.default-release\cache2.json"),
            No(R + @"\Mozilla\Firefox\Profiles\abc.default-release\places.sqlite"),
        ]),
        ["discord"] = ("electron.discord.cache",
            [Yes(R + @"\discord\Cache\Cache_Data\f_1"), Yes(R + @"\discord\GPUCache\data_1"), No(R + @"\discord\Local Storage\leveldb\000003.log")]),
        ["vscode"] = ("electron.vscode.cache",
            [Yes(R + @"\Code\CachedData\abc\x.code"), Yes(R + @"\Code\Cache\Cache_Data\f"), No(R + @"\Code\User\settings.json"), No(R + @"\Code\User\History\a\entries.json")]),
        ["slack"] = ("electron.slack.cache",
            [Yes(R + @"\Slack\Service Worker\CacheStorage\a\b"), Yes(R + @"\Slack\Cache\Cache_Data\f"), No(R + @"\Slack\Service Worker\Database\b"), No(R + @"\Slack\storage\slack-workspaces")]),
        ["d3dscache"] = ("gpu.directx.shadercache", [Yes(L + @"\D3DSCache\abc\x.idx"), Yes(L + @"\D3DSCache\abc\x.val")]),
        ["nvidia-cache"] = ("gpu.nvidia.shadercache", [Yes(L + @"\NVIDIA\DXCache\a.bin"), Yes(L + @"\NVIDIA\GLCache\b\c.bin"), No(L + @"\NVIDIA\NvBackend\settings.json")]),
        ["amd-cache"] = ("gpu.amd.shadercache", [Yes(L + @"\AMD\VkCache\a"), Yes(L + @"\AMD\DxcCache\b"), No(L + @"\AMD\CN\settings.xml")]),
        ["thumbcache"] = ("windows.explorer.thumbcache",
        [
            Yes(L + @"\Microsoft\Windows\Explorer\thumbcache_256.db"), Yes(L + @"\Microsoft\Windows\Explorer\iconcache_32.db"),
            No(L + @"\Microsoft\Windows\Explorer\ExplorerStartupLog.etl"), No(L + @"\Microsoft\Windows\Explorer\sub\thumbcache_x.db"),
        ]),
        ["crashdumps"] = ("windows.user.crashdumps",
            [Yes(L + @"\CrashDumps\app.exe.123.dmp"), No(L + @"\CrashDumps\new.dmp", age: 1), No(L + @"\CrashDumps\readme.txt")]),
        ["wer"] = ("windows.user.wer",
        [
            Yes(L + @"\Microsoft\Windows\WER\ReportArchive\r1\Report.wer"), Yes(L + @"\Microsoft\Windows\WER\ReportQueue\q\x.cab"),
            No(L + @"\Microsoft\Windows\WER\ReportQueue\q\new.cab", age: 1), No(L + @"\Microsoft\Windows\WER\Temp\y"),
        ]),
        ["vivaldi-profile"] = ("browser.chromium.vivaldi.cache", Chromium(L + @"\Vivaldi\User Data")),
        ["opera"] = ("browser.opera.cache",
            [Yes(L + @"\Opera Software\Opera Stable\Cache\Cache_Data\f_1"), Yes(L + @"\Opera Software\Opera Stable\GPUCache\d"), No(L + @"\Opera Software\Opera Stable\Bookmarks")]),
        ["teams-classic"] = ("electron.teams.classic.cache",
            [Yes(R + @"\Microsoft\Teams\Cache\f_1"), Yes(R + @"\Microsoft\Teams\Service Worker\CacheStorage\a"), No(R + @"\Microsoft\Teams\desktop-config.json"), No(R + @"\Microsoft\Teams\IndexedDB\x")]),
        ["spotify"] = ("app.spotify.cache",
            [Yes(L + @"\Spotify\Data\a\b.file"), Yes(L + @"\Spotify\Storage\c.file"), No(L + @"\Spotify\Users\u-user\offline.bnk"), No(L + @"\Spotify\prefs")]),
        ["inetcache"] = ("windows.user.inetcache",
            [Yes(L + @"\Microsoft\Windows\INetCache\IE\ABC123\x.htm"), Yes(L + @"\Microsoft\Windows\INetCache\Low\IE\D\y.js"), No(L + @"\Microsoft\Windows\INetCache\Content.Outlook\X\attachment.pdf")]),
        ["intel-cache"] = ("gpu.intel.shadercache", [Yes(L + @"\Intel\ShaderCache\app\a.bin"), No(L + @"\Intel\GCC\settings.json")]),
        ["npm"] = ("dev.npm.cache", [Yes(L + @"\npm-cache\_cacache\content-v2\sha512\ab\cd"), No(L + @"\npm-cache\_logs\debug.log")]),
        ["pip"] = ("dev.pip.cache", [Yes(L + @"\pip\Cache\http-v2\a\b"), No(L + @"\pip\pip.ini")]),
        ["nuget"] = ("dev.nuget.httpcache",
            [Yes(L + @"\NuGet\v3-cache\abc$ps_api.nuget.org\list.dat"), Yes(L + @"\NuGet\plugins-cache\x"), No(L + @"\NuGet\Config\a.config"), No(@"C:\Users\u\.nuget\packages\newtonsoft.json\13.0.3\lib.dll")]),
        ["yarn"] = ("dev.yarn.cache", [Yes(L + @"\Yarn\Cache\v6\npm-left-pad\index.js"), No(L + @"\Yarn\Data\global\package.json")]),
        ["minidump"] = ("windows.system.minidump", [Yes(W + @"\Minidump\102326-1.dmp"), No(W + @"\Minidump\fresh.dmp", age: 1)]),
        ["gradle"] = ("dev.gradle.cache", [Yes(@"C:\Users\u\.gradle\caches\modules-2\files-2.1\a\b.jar"), No(@"C:\Users\u\.gradle\gradle.properties"), No(@"C:\Users\u\.gradle\wrapper\dists\g\gradle.bin")]),
        ["maven"] = ("dev.maven.repository", [Yes(@"C:\Users\u\.m2\repository\org\x\x.jar", 90), No(@"C:\Users\u\.m2\settings.xml")]),
        ["cargo"] = ("dev.cargo.registry", [Yes(@"C:\Users\u\.cargo\registry\cache\idx\serde.crate"), Yes(@"C:\Users\u\.cargo\registry\src\idx\serde\lib.rs"), No(@"C:\Users\u\.cargo\config.toml"), No(@"C:\Users\u\.cargo\bin\cargo.exe")]),
        ["go-build"] = ("dev.go.buildcache", [Yes(@"C:\Users\u\AppData\Local\go-build\ab\abcdef-d")]),
        ["pnpm"] = ("dev.pnpm.store", [Yes(@"C:\Users\u\AppData\Local\pnpm\store\v3\files\ab\cdef"), No(@"C:\Users\u\AppData\Local\pnpm\pnpm.exe"), No(@"C:\Users\u\AppData\Local\pnpm\global\5\package.json")]),
        ["huggingface"] = ("dev.huggingface.hub", [Yes(@"C:\Users\u\.cache\huggingface\hub\models--x\blobs\abc", 90), No(@"C:\Users\u\.cache\huggingface\token")]),
        ["playwright"] = ("dev.playwright.browsers", [Yes(@"C:\Users\u\AppData\Local\ms-playwright\chromium-1000\chrome-win\chrome.exe")]),
        ["cypress"] = ("dev.cypress.cache", [Yes(@"C:\Users\u\AppData\Local\Cypress\Cache\13.0.0\Cypress\Cypress.exe"), No(@"C:\Users\u\AppData\Local\Cypress\other.txt")]),
        ["electron-dl"] = ("dev.electron.cache", [Yes(@"C:\Users\u\AppData\Local\electron\Cache\abc\electron-v1-win32-x64.zip"), No(@"C:\Users\u\AppData\Local\electron\settings.json")]),
        ["node-gyp"] = ("dev.nodegyp.cache", [Yes(@"C:\Users\u\AppData\Local\node-gyp\Cache\20.0.0\include\node\node.h"), No(@"C:\Users\u\AppData\Local\node-gyp\config.gypi")]),
        ["jetbrains"] = ("dev.jetbrains.caches", [Yes(@"C:\Users\u\AppData\Local\JetBrains\IntelliJIdea2024.1\caches\index\x"), Yes(@"C:\Users\u\AppData\Local\JetBrains\Rider2024.1\log\idea.log"), No(@"C:\Users\u\AppData\Local\JetBrains\IntelliJIdea2024.1\options\ide.general.xml"), No(@"C:\Users\u\AppData\Local\JetBrains\IntelliJIdea2024.1\plugins\p\p.jar")]),
        ["vs-cmc"] = ("dev.vs.componentmodelcache", [Yes(@"C:\Users\u\AppData\Local\Microsoft\VisualStudio\17.0_abc123\ComponentModelCache\Microsoft.VisualStudio.Default.cache"), No(@"C:\Users\u\AppData\Local\Microsoft\VisualStudio\17.0_abc123\Settings\CurrentSettings.vssettings")]),
        ["unity"] = ("dev.unity.cache", [Yes(@"C:\Users\u\AppData\Local\Unity\cache\packages\a\b.tgz"), No(@"C:\Users\u\AppData\Local\Unity\Editor\Editor.log")]),
        ["unreal-ddc"] = ("dev.unreal.ddc", [Yes(@"C:\Users\u\AppData\Local\UnrealEngine\Common\DerivedDataCache\ab\cd\x.udd", 90), No(@"C:\Users\u\AppData\Local\UnrealEngine\5.3\Saved\Config\Windows\Engine.ini")]),
        ["composer"] = ("dev.composer.cache", [Yes(@"C:\Users\u\AppData\Local\Composer\files\vendor\pkg\1.0.zip"), Yes(@"C:\Users\u\AppData\Local\Composer\repo\https---repo.packagist.org\p.json"), No(@"C:\Users\u\AppData\Local\Composer\config.json")]),
        ["nuget-packages"] = ("dev.nuget.packages", [Yes(@"C:\Users\u\.nuget\packages\newtonsoft.json\13.0.1\lib\net6.0\Newtonsoft.Json.dll", 90), No(@"C:\Users\u\.nuget\NuGet\NuGet.Config")]),
        ["epic-webcache"] = ("launcher.epic.webcache", [Yes(@"C:\Users\u\AppData\Local\EpicGamesLauncher\Saved\webcache_4430\Cache\Cache_Data\f_1"), No(@"C:\Users\u\AppData\Local\EpicGamesLauncher\Saved\Config\Windows\GameUserSettings.ini")]),
        ["steam-html"] = ("launcher.steam.htmlcache", [Yes(@"C:\Users\u\AppData\Local\Steam\htmlcache\Cache\Cache_Data\f_1"), No(@"C:\Users\u\AppData\Local\Steam\other.txt")]),
        ["telegram"] = ("app.telegram.cache", [Yes(@"C:\Users\u\AppData\Roaming\Telegram Desktop\tdata\user_data\cache\0\x"), Yes(@"C:\Users\u\AppData\Roaming\Telegram Desktop\tdata\user_data\media_cache\0\y"), No(@"C:\Users\u\AppData\Roaming\Telegram Desktop\tdata\key_datas"), No(@"C:\Users\u\AppData\Roaming\Telegram Desktop\tdata\D877F783D5D3EF8C\map")]),
        ["adobe-media"] = ("app.adobe.mediacache", [Yes(@"C:\Users\u\AppData\Roaming\Adobe\Common\Media Cache Files\a.cfa"), Yes(@"C:\Users\u\AppData\Roaming\Adobe\Common\Peak Files\b.pek"), No(@"C:\Users\u\AppData\Roaming\Adobe\Common\Presets\p.prfpset")]),
        ["cryptnet"] = ("windows.user.cryptneturlcache", [Yes(@"C:\Users\u\AppData\LocalLow\Microsoft\CryptnetUrlCache\Content\AB12"), Yes(@"C:\Users\u\AppData\LocalLow\Microsoft\CryptnetUrlCache\MetaData\AB12"), No(@"C:\Users\u\AppData\LocalLow\Microsoft\CryptnetUrlCache\other")]),
    };
}

// In-memory IFileTree: directories are implied by file paths.
public sealed class FakeTree(IEnumerable<FixtureFile> files, DateTime now) : IFileTree
{
    private readonly FileEntry[] entries = [.. files.Select(f => new FileEntry(f.Path, 1000, f.Attributes, now.AddDays(-f.AgeDays)))];

    public bool Exists(string path) => entries.Any(e => ProtectedPaths.IsUnder(e.Path, path));

    public IEnumerable<string> Subdirectories(string path) => entries
        .Where(e => e.Path.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) && e.Path.IndexOf('\\', path.Length + 1) > 0)
        .Select(e => e.Path[..e.Path.IndexOf('\\', path.Length + 1)])
        .Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<FileEntry> Files(string root, CancellationToken cancellation) =>
        entries.Where(e => e.Path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
}

public class RuleFixtureTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CleanupCatalog Catalog = CatalogLoader.LoadBuiltIn();

    public static TheoryData<string> Names => [.. RuleFixtures.All.Keys];

    [Theory]
    [MemberData(nameof(Names))]
    public void Rule_selects_exactly_the_fixture_picks(string fixture)
    {
        (string ruleId, FixtureFile[] files) = RuleFixtures.All[fixture];
        Assert.Contains(fixture, Catalog.Rules.Single(r => r.Id == ruleId).Fixtures);

        RulePreview preview = Assert.Single(Preview(files, ProtectedPathsTests.Folders, r => r.Id == ruleId));
        Assert.Equal(files.Where(f => f.Pick).Select(f => f.Path).Order(), preview.Items.Select(i => i.Path).Order());
    }

    // Without a fixture's tree the rule is not detected and yields nothing at all.
    [Fact]
    public void Undetected_rules_are_left_out() => Assert.Empty(Preview([], ProtectedPathsTests.Folders, _ => true));

    // A rule root that falls inside a protected folder selects nothing, whatever the catalog says.
    [Fact]
    public void Protected_paths_win_over_rules()
    {
        var folders = ProtectedPathsTests.Folders with { CloudRoots = [@"C:\Users\u\AppData\Local\Temp"] };
        FixtureFile[] files = [Yes(@"C:\Users\u\AppData\Local\Temp\a.tmp")];
        RulePreview preview = Assert.Single(Preview(files, folders, r => r.Id == "windows.user.temp"));
        Assert.Empty(preview.Items);
        Assert.Equal(1, preview.Protected);
    }

    [Fact]
    public void Too_recent_and_deferred_are_counted()
    {
        (_, FixtureFile[] files) = RuleFixtures.All["user-temp"];
        RulePreview preview = Assert.Single(Preview(files, ProtectedPathsTests.Folders, r => r.Id == "windows.user.temp"));
        Assert.Equal((1L, 1L, 2000L), (preview.TooRecent, preview.Deferred, preview.LogicalBytes));
    }

    // FR-CLN-014: running apps and file holders are reported; only running processes from the rule count.
    [Fact]
    public void Running_processes_and_lock_holders_are_reported()
    {
        (_, FixtureFile[] files) = RuleFixtures.All["chromium-profile"];
        var locks = new FakeLocks(files[0].Path);
        RulePreview preview = Assert.Single(new CleanupPreviewer(Catalog, new FakeTree(files, Now), ProtectedPathsTests.Folders, new FixedTime(Now),
            new FakeProcesses("chrome.exe"), locks).Preview(TestContext.Current.CancellationToken, r => r.Id == "browser.chromium.chrome.cache"));
        Assert.Equal(["chrome.exe"], preview.Running);
        Assert.Equal([new LockingProcess(42, "Google Chrome")], preview.LockedBy);
        Assert.Equal(preview.Items.Count, preview.LockChecked);
        Assert.Equal(preview.Items.Select(i => i.Path).Order(), locks.Asked.Order());
    }

    [Fact]
    public void Nothing_is_reported_when_apps_are_closed()
    {
        (_, FixtureFile[] files) = RuleFixtures.All["chromium-profile"];
        RulePreview preview = Assert.Single(new CleanupPreviewer(Catalog, new FakeTree(files, Now), ProtectedPathsTests.Folders, new FixedTime(Now),
            new FakeProcesses(), new FakeLocks()).Preview(TestContext.Current.CancellationToken, r => r.Id == "browser.chromium.chrome.cache"));
        Assert.Empty(preview.Running);
        Assert.Empty(preview.LockedBy);
    }

    private sealed class FakeProcesses(params string[] running) : IProcessList
    {
        public bool IsRunning(string imageName) => running.Contains(imageName, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FakeLocks(params string[] locked) : ILockInspector
    {
        public List<string> Asked { get; } = [];

        public IReadOnlyList<LockingProcess> WhoLocks(IReadOnlyList<string> paths)
        {
            Asked.AddRange(paths);
            return paths.Any(p => locked.Contains(p)) ? [new(42, "Google Chrome")] : [];
        }
    }

    private static FixtureFile Yes(string path) => new(path, 30, Pick: true);

    private static IReadOnlyList<RulePreview> Preview(IEnumerable<FixtureFile> files, UserFolders folders, Func<CleanupRule, bool> only) =>
        new CleanupPreviewer(Catalog, new FakeTree(files, Now), folders, new FixedTime(Now)).Preview(TestContext.Current.CancellationToken, only);

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
