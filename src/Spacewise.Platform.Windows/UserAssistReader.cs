using Microsoft.Win32;
using Spacewise.Core;

namespace Spacewise.Platform.Windows;

// Reads the current user's UserAssist counters (HKCU, no elevation, ADR 0001). Only the current SID is ever read (FR-USE-008).
public static class UserAssistReader
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    // null: the key could not be read at all, which the analyzer reports as UnknownHistory, not as "never used".
    public static IReadOnlyList<UsageObservation>? Read()
    {
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (root is null) return null;
            var folders = KnownFolders();
            var byPath = new Dictionary<string, UsageObservation>(StringComparer.OrdinalIgnoreCase);
            foreach (string guid in root.GetSubKeyNames())
            {
                using RegistryKey? count = root.OpenSubKey($@"{guid}\Count");
                if (count is null) continue;
                foreach (string encoded in count.GetValueNames())
                {
                    if (count.GetValue(encoded) is not byte[] data || UserAssistParser.ParseValue(data) is not var (runs, last)) continue;
                    string path = UserAssistParser.ExpandKnownFolder(UserAssistParser.DecodeName(encoded), folders);
                    if (!UserAssistParser.IsExecutablePath(path)) continue;
                    byPath[path] = byPath.TryGetValue(path, out UsageObservation? existing)
                        ? new(path, existing.LastRunUtc > last ? existing.LastRunUtc : last, existing.RunCount + runs)
                        : new(path, last, runs);
                }
            }
            return [.. byPath.Values];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    // KNOWNFOLDERID values that UserAssist uses as path prefixes.
    public static IReadOnlyDictionary<string, string> KnownFolders()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.GetEnvironmentVariable("ProgramW6432") ?? programFiles,
            ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = x86,
            ["{DE974D24-D9C6-4D3E-BF91-F4455120B917}"] = Path.Combine(programFiles, "Common Files"),
            ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Path.Combine(windows, "System32"),
            ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Path.Combine(windows, "SysWOW64"),
            ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = windows,
            ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["{62AB5D82-FDC1-4DC3-A9DD-070D1D495D97}"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["{5E6C858F-0E22-4760-9AFE-EA3317B67173}"] = profile,
            ["{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}"] = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ["{FDD39AD0-238F-46AF-ADB4-6C85480369C7}"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["{374DE290-123F-4565-9164-39C4925E467B}"] = Path.Combine(profile, "Downloads"),
        };
    }
}
