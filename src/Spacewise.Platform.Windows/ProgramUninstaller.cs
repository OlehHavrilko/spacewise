using System.Diagnostics;
using Spacewise.Core;

namespace Spacewise.Platform.Windows;

public sealed record RemnantLocation(string Path, bool CanOfferRemoval);

public static class ProgramUninstaller
{
    public static async Task<int?> RunAsync(InstalledProgram confirmed)
    {
        InstalledProgram? current = ProgramInventory.ReadRegistration(confirmed.Id);
        if (current is null || current.Name != confirmed.Name || current.Publisher != confirmed.Publisher
            || current.MsiProductCode != confirmed.MsiProductCode || current.RegisteredInstallLocation != confirmed.RegisteredInstallLocation
            || current.UninstallString != confirmed.UninstallString || Prepare(current) is not { } command) return null;

        using Process process = Process.Start(command) ?? throw new InvalidOperationException("Не удалось запустить деинсталлятор.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    public static ProcessStartInfo? Prepare(InstalledProgram program)
    {
        if (program.Kind != ProgramKind.Win32) return null;
        if (program.MsiProductCode is Guid code)
            return new(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/x {code:B} /norestart")
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.SystemDirectory,
            };

        if (program.UninstallString?.Trim() is not { Length: > 0 } command
            || !IsLocalPath(program.RegisteredInstallLocation)) return null;
        bool quoted = command.StartsWith('"');
        int end = quoted ? command.IndexOf('"', 1) : command.AsSpan().IndexOfAny(' ', '\t');
        if (quoted && (end <= 1 || end + 1 < command.Length && !char.IsWhiteSpace(command[end + 1]))) return null;
        if (end < 0) end = command.Length;
        string file = command[(quoted ? 1 : 0)..end];
        UserFolders folders = UserFolders.FromEnvironment();
        if (!IsLocalPath(file) || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !ProtectedPaths.IsUnder(file, program.RegisteredInstallLocation!)
            || ProtectedPaths.IsUnder(file, folders.SystemRoot) || !File.Exists(file)) return null;

        // Execute the registered binary directly, with no command interpreter or search on PATH.
        for (string? path = file; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        return new(file, command[(quoted ? end + 1 : end)..].TrimStart())
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(file)!,
        };
    }

    public static IReadOnlyList<RemnantLocation> RemainingLocations(InstalledProgram removed, IReadOnlyList<InstalledProgram> installed, UserFolders folders)
    {
        string[] roots = [folders.ProgramFiles, folders.ProgramFilesX86, folders.LocalAppData, folders.RoamingAppData, folders.ProgramData];
        string[] occupied = [.. installed.SelectMany(p => SuggestedLocations(p, folders).Select(l => l.Path)
            .Append(p.InstallLocation)).OfType<string>()];
        return [.. SuggestedLocations(removed, folders)
            .Where(location => IsLocalPath(location.Path)
                && roots.Any(root => root.Length > 0 && !location.Path.Equals(root, StringComparison.OrdinalIgnoreCase)
                    && ProtectedPaths.IsUnder(location.Path, root))
                && !Overlaps(location.Path, folders.Spacewise)
                && !folders.CloudRoots.Any(root => Overlaps(location.Path, root))
                && !occupied.Any(other => Overlaps(location.Path, other)))
            .GroupBy(location => location.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RemnantLocation(group.Key, group.Any(location => location.CanOfferRemoval)))];
    }

    private static IEnumerable<RemnantLocation> SuggestedLocations(InstalledProgram program, UserFolders folders)
    {
        if (program.RegisteredInstallLocation is { } registered) yield return new(registered, false);
        if (!SafeSegment(program.Publisher) || !SafeSegment(program.Name)) yield break;
        foreach (string root in new[] { folders.LocalAppData, folders.RoamingAppData, folders.ProgramData })
        {
            if (root.Length == 0) continue;
            yield return new(Path.Combine(root, program.Publisher!, program.Name),
                !root.Equals(folders.ProgramData, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool SafeSegment(string? value) => value is { Length: > 0 } && value == value.Trim()
        && !value.EndsWith('.') && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !value.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("Packages", StringComparison.OrdinalIgnoreCase);

    private static bool Overlaps(string first, string second) => ProtectedPaths.IsUnder(first, second) || ProtectedPaths.IsUnder(second, first);

    private static bool IsLocalPath(string? path)
    {
        if (path is not { Length: > 3 } || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.AsSpan(3).IndexOfAny(':', '/') >= 0) return false;
        try
        {
            return string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase)
                && new DriveInfo(path[..3]).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }
}
