using System.Globalization;
using System.Text;

namespace Spacewise.Core;

public enum ProgramKind { Win32, Steam }

public sealed record InstalledProgram(string Id, string Name, string? Publisher, string? InstallLocation, ProgramKind Kind,
    long? SizeBytes, string? UninstallString);

public sealed record UsageObservation(string ExePath, DateTime LastRunUtc, int RunCount);

// ТЗ §5.2: the status says what was seen and over which window; it never says "unused" without coverage.
public enum ObservationStatus { ObservedActive, NoActivityObservedWithinCoveredWindow, UnknownHistory, InsufficientCoverage }

public enum EvidenceLevel { Unknown, Heuristic, Corroborated, DirectEvidence }

public sealed record ProgramUsage(InstalledProgram Program, ObservationStatus Status, EvidenceLevel Evidence, DateTime? LastSeenUtc,
    int RunCount, string Source, int WindowDays)
{
    public string Describe(DateTime nowUtc) => Status switch
    {
        ObservationStatus.ObservedActive when LastSeenUtc is { } seen =>
            $"Запускалась {DaysAgo(nowUtc, seen)} · {Source}",
        ObservationStatus.NoActivityObservedWithinCoveredWindow when LastSeenUtc is { } seen =>
            $"Последний запуск {DaysAgo(nowUtc, seen)} · {Source}",
        ObservationStatus.NoActivityObservedWithinCoveredWindow =>
            $"Запуски не замечены за {WindowDays} дн. наблюдения · {Source}",
        ObservationStatus.InsufficientCoverage => "Данных пока мало",
        _ => "История запусков неизвестна",
    };

    private static string DaysAgo(DateTime now, DateTime seen)
    {
        int days = Math.Max(0, (int)(now - seen).TotalDays);
        return days == 0 ? "сегодня" : $"{days} дн. назад";
    }
}

public sealed record UnusedSummary(int Programs, long Bytes, int WithoutSize);

// UserAssist, the only launch source readable without administrator rights (ADR 0001). It covers launches through
// the shell only, so every result built from it is Heuristic evidence.
public static class UserAssistParser
{
    public const string SourceName = "UserAssist";
    private static readonly long MinimumFileTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    // Value names are ROT13: letters rotate within their alphabet, everything else stays.
    public static string DecodeName(string encoded)
    {
        var builder = new StringBuilder(encoded.Length);
        foreach (char c in encoded)
            builder.Append(c switch
            {
                >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                _ => c,
            });
        return builder.ToString();
    }

    // The 72-byte (Windows 7 and later) record: run count at 4, last execution FILETIME at 60.
    public static (int RunCount, DateTime LastRunUtc)? ParseValue(ReadOnlySpan<byte> data)
    {
        if (data.Length < 68) return null;
        long fileTime = BitConverter.ToInt64(data[60..68]);
        // Counters that were never launched carry tiny non-zero timestamps; nothing before 2000 is a real launch.
        if (fileTime < MinimumFileTime) return null;
        try { return (checked((int)BitConverter.ToUInt32(data[4..8])), DateTime.FromFileTimeUtc(fileTime)); }
        catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException) { return null; }
    }

    // "{KNOWNFOLDERID}\sub\app.exe" becomes a real path when the GUID is known; other names are returned as they are.
    public static string ExpandKnownFolder(string decodedName, IReadOnlyDictionary<string, string> knownFolders)
    {
        if (decodedName.Length > 38 && decodedName[0] == '{' && decodedName[37] == '}' && decodedName[38] == '\\'
            && knownFolders.TryGetValue(decodedName[..38], out string? folder))
            return Path.TrimEndingDirectorySeparator(folder) + decodedName[38..];
        return decodedName;
    }

    public static bool IsExecutablePath(string path) =>
        path.Length > 3 && (path[1] == ':' && path[2] == '\\') && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}

public static class UsageAnalyzer
{
    public const int DefaultThresholdDays = 90;
    public const int MinimumCoverageDays = 14;

    // observations == null means the source could not be read at all.
    public static IReadOnlyList<ProgramUsage> Analyze(IReadOnlyList<InstalledProgram> programs, IReadOnlyList<UsageObservation>? observations,
        DateTime nowUtc, int thresholdDays = DefaultThresholdDays)
    {
        if (observations is null)
            return [.. programs.Select(p => new ProgramUsage(p, ObservationStatus.UnknownHistory, EvidenceLevel.Unknown, null, 0, "", 0))];

        // The oldest record is a lower bound for how far back the source reaches.
        int windowDays = observations.Count == 0 ? 0 : Math.Max(0, (int)(nowUtc - observations.Min(o => o.LastRunUtc)).TotalDays);
        var matched = new Dictionary<InstalledProgram, (DateTime Last, int Runs)>();
        foreach (UsageObservation observation in observations)
        {
            InstalledProgram? program = Match(programs, observation.ExePath);
            if (program is null) continue;
            matched[program] = matched.TryGetValue(program, out var current)
                ? (observation.LastRunUtc > current.Last ? observation.LastRunUtc : current.Last, current.Runs + observation.RunCount)
                : (observation.LastRunUtc, observation.RunCount);
        }

        var result = new List<ProgramUsage>(programs.Count);
        foreach (InstalledProgram program in programs)
        {
            if (string.IsNullOrEmpty(program.InstallLocation))
            {
                // No install path: a launch could not be attributed to this program, so absence proves nothing.
                result.Add(new(program, ObservationStatus.UnknownHistory, EvidenceLevel.Unknown, null, 0, "", 0));
                continue;
            }
            if (matched.TryGetValue(program, out var seen))
            {
                bool recent = (nowUtc - seen.Last).TotalDays <= thresholdDays;
                result.Add(new(program, recent ? ObservationStatus.ObservedActive : ObservationStatus.NoActivityObservedWithinCoveredWindow,
                    EvidenceLevel.Heuristic, seen.Last, seen.Runs, UserAssistParser.SourceName, windowDays));
            }
            else
            {
                ObservationStatus status = windowDays >= thresholdDays ? ObservationStatus.NoActivityObservedWithinCoveredWindow
                    : windowDays >= MinimumCoverageDays ? ObservationStatus.InsufficientCoverage
                    : ObservationStatus.UnknownHistory;
                result.Add(new(program, status, status == ObservationStatus.UnknownHistory ? EvidenceLevel.Unknown : EvidenceLevel.Heuristic,
                    null, 0, status == ObservationStatus.UnknownHistory ? "" : UserAssistParser.SourceName, windowDays));
            }
        }
        return result;
    }

    // The longest InstallLocation prefix wins (ТЗ §5.2).
    public static InstalledProgram? Match(IReadOnlyList<InstalledProgram> programs, string exePath)
    {
        InstalledProgram? best = null;
        int bestLength = -1;
        foreach (InstalledProgram program in programs)
        {
            if (string.IsNullOrEmpty(program.InstallLocation)) continue;
            string root = Path.TrimEndingDirectorySeparator(program.InstallLocation);
            if (root.Length <= 3 || !ProtectedPaths.IsUnder(exePath, root) || root.Length <= bestLength) continue;
            best = program;
            bestLength = root.Length;
        }
        return best;
    }

    // FR-USE-005: "N программ не запускались 90+ дней · X ГБ", only for programs whose absence is backed by coverage.
    public static UnusedSummary Unused(IEnumerable<ProgramUsage> usages) =>
        usages.Where(u => u.Status == ObservationStatus.NoActivityObservedWithinCoveredWindow)
            .Aggregate(new UnusedSummary(0, 0, 0), (sum, u) => new(sum.Programs + 1, sum.Bytes + (u.Program.SizeBytes ?? 0),
                sum.WithoutSize + (u.Program.SizeBytes is null ? 1 : 0)));
}
