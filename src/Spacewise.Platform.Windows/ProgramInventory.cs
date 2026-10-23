using System.Text.RegularExpressions;
using Microsoft.Win32;
using Spacewise.Core;

namespace Spacewise.Platform.Windows;

// FR-USE-001: Uninstall keys (HKLM, WOW6432Node, HKCU) and Steam appmanifests. Reads registry metadata and small text
// files only; sizes are measured separately because walking an install folder can take minutes.
public static partial class ProgramInventory
{
    private static readonly (RegistryHive Hive, RegistryView View, string Label)[] Roots =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM-WOW64"),
        (RegistryHive.CurrentUser, RegistryView.Default, "HKCU"),
    ];

    public static IReadOnlyList<InstalledProgram> Read()
    {
        var programs = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((RegistryHive hive, RegistryView view, string label) in Roots)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? key = uninstall.OpenSubKey(name);
                    if (key is null || FromUninstallKey($"{label}\\{name}", name, key) is not { } program) continue;
                    if (seen.Add($"{program.Name}|{program.InstallLocation}")) programs.Add(program);
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        foreach (InstalledProgram steam in ReadSteam())
            if (seen.Add($"{steam.Name}|{steam.InstallLocation}")) programs.Add(steam);
        return [.. programs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static InstalledProgram? FromUninstallKey(string id, string keyName, RegistryKey key)
    {
        string? name = key.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(name)) return null;
        // Updates, components and Steam's own per-game entries are not separate programs.
        if (key.GetValue("SystemComponent") is 1 || key.GetValue("ParentKeyName") is not null || key.GetValue("ReleaseType") is string) return null;
        if (keyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)) return null;
        string? uninstall = key.GetValue("UninstallString") as string;
        string? location = Clean(key.GetValue("InstallLocation") as string)
            ?? DirectoryOf(key.GetValue("DisplayIcon") as string) ?? DirectoryOf(uninstall);
        long? size = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
        return new(id, name.Trim(), (key.GetValue("Publisher") as string)?.Trim(), location, ProgramKind.Win32, size, uninstall);
    }

    // The folder of an executable named in a registry string; Windows' own folders are never an install location.
    private static string? DirectoryOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string path = command.Trim();
        if (path.StartsWith('"')) { int close = path.IndexOf('"', 1); path = close > 0 ? path[1..close] : path[1..]; }
        else { int exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (exe > 0) path = path[..(exe + 4)]; }
        path = path.Split(',')[0];
        string? directory = Path.IsPathFullyQualified(path) ? Path.GetDirectoryName(path) : null;
        if (directory is null) return null;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return ProtectedPaths.IsUnder(directory, windows) || directory.Length <= 3 ? null : directory;
    }

    private static string? Clean(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string trimmed = path.Trim().Trim('"');
        return Path.IsPathFullyQualified(trimmed) && trimmed.Length > 3 ? Path.TrimEndingDirectorySeparator(trimmed) : null;
    }

    public static IReadOnlyList<InstalledProgram> ReadSteam()
    {
        var result = new List<InstalledProgram>();
        try
        {
            using RegistryKey? steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steamKey?.GetValue("SteamPath") is not string steamPath) return result;
            var libraries = new List<string> { steamPath.Replace('/', '\\') };
            string libraryFile = Path.Combine(libraries[0], "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
                foreach (Match m in LibraryPath().Matches(File.ReadAllText(libraryFile)))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\").Replace('/', '\\'));
            foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string apps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                    if (ParseManifest(File.ReadAllText(manifest), apps) is { } game) result.Add(game);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return result;
    }

    // Public for tests: a manifest is a flat "key" "value" list in Valve's text format.
    public static InstalledProgram? ParseManifest(string text, string steamApps)
    {
        string? Field(string key) => Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
        string? name = Field("name"), directory = Field("installdir"), appId = Field("appid");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(directory) || directory.Contains("..") || directory.Contains('\\') || directory.Contains('/'))
            return null;
        long? size = long.TryParse(Field("SizeOnDisk"), out long bytes) && bytes > 0 ? bytes : null;
        return new($"steam\\{appId}", name, "Steam", Path.Combine(steamApps, "common", directory), ProgramKind.Steam, size, null);
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

    // Bounded metadata walk (no file content). Returns what was counted and whether the cap stopped the walk.
    public static (long Bytes, bool Complete) Measure(string location, IFileTree tree, CancellationToken cancellation, int maximumFiles = 500_000)
    {
        long bytes = 0;
        int files = 0;
        foreach (FileEntry file in tree.Files(location, cancellation))
        {
            bytes += file.Length;
            if (++files >= maximumFiles) return (bytes, false);
        }
        return (bytes, true);
    }
}
