using System.Text.RegularExpressions;

namespace Spacewise.Core;

// Folders the policy and the catalog resolve against. Spacewise is the app's own data folder (with quarantine).
public sealed record UserFolders(string Profile, string LocalAppData, string RoamingAppData, string Temp,
    string SystemRoot, string ProgramData, string ProgramFiles, string ProgramFilesX86, string Spacewise,
    IReadOnlyList<string> CloudRoots)
{
    public static UserFolders FromEnvironment()
    {
        static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        string local = Folder(Environment.SpecialFolder.LocalApplicationData);
        string[] cloud = [.. new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable).OfType<string>().Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        return new(Folder(Environment.SpecialFolder.UserProfile), local, Folder(Environment.SpecialFolder.ApplicationData),
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Folder(Environment.SpecialFolder.Windows),
            Folder(Environment.SpecialFolder.CommonApplicationData), Folder(Environment.SpecialFolder.ProgramFiles),
            Folder(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(local, "Spacewise"), cloud);
    }
}

// Spec §9: deny-by-default filter for cleanup targets. A path is allowed only when it is canonical,
// lies under an allowed root and under no protected one. The executor checks again by handle.
public static partial class ProtectedPaths
{
    // %SystemRoot% subfolders a catalog rule may target; Prefetch never.
    private static readonly string[] SystemRootAllowed = ["Temp", "LiveKernelReports", "Minidump"];
    private static readonly string[] VolumeProtected = ["System Volume Information", "$Recycle.Bin",
        "pagefile.sys", "swapfile.sys", "hiberfil.sys"];

    public static PolicyDecision Check(string path, UserFolders folders)
    {
        if (!CanonicalDrivePath().IsMatch(path))
            return Deny("Не локальный абсолютный путь (UNC, device path, \\\\?\\ или относительный).");
        string[] segments = path[3..].Split('\\');
        if (path.Length == 3 || segments.Length == 0)
            return Deny("Корень тома.");
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                return Deny("Пустой сегмент или переход по каталогам.");
            if (segment.EndsWith('.') || segment.EndsWith(' '))
                return Deny("Имя с точкой или пробелом в конце.");
            if (ShortName().IsMatch(segment))
                return Deny("Короткое имя 8.3.");
        }
        if (VolumeProtected.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
            return Deny($"Системный объект тома: {segments[0]}.");

        if (IsUnder(path, folders.Spacewise)) return Deny("Папка Spacewise и карантин.");
        if (folders.CloudRoots.Any(root => IsUnder(path, root))) return Deny("Облачная папка.");
        if (IsUnder(path, folders.SystemRoot))
            return SystemRootAllowed.Any(sub => IsUnder(path, Path.Combine(folders.SystemRoot, sub)) && !SamePath(path, Path.Combine(folders.SystemRoot, sub)))
                ? Allow() : Deny("Системная папка Windows.");
        if (IsUnder(path, folders.ProgramData) || IsUnder(path, folders.ProgramFiles) || IsUnder(path, folders.ProgramFilesX86))
            return Deny("Program Files и ProgramData.");
        if (IsUnder(path, folders.Profile) && !SamePath(path, folders.Profile)) return Allow();
        return Deny("Вне разрешённых корней.");
    }

    // Case-insensitive, on a segment boundary: C:\Temp2 is not under C:\Temp.
    public static bool IsUnder(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        return SamePath(path, root) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static PolicyDecision Allow() => new(true, "Разрешённый корень.");
    private static PolicyDecision Deny(string reason) => new(false, reason);

    // Drive letter, colon, backslash; no other colon (ADS), no forward slash.
    [GeneratedRegex(@"^[A-Za-z]:\\[^:/]*$")]
    private static partial Regex CanonicalDrivePath();

    [GeneratedRegex(@"~\d")]
    private static partial Regex ShortName();
}
