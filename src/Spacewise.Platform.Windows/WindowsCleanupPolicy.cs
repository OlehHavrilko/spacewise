using Spacewise.Core;

namespace Spacewise.Platform.Windows;

public sealed class WindowsCleanupPolicy(UserFolders folders, Func<IReadOnlyList<string>> exclusions, string applicationDirectory) : ICleanupPathPolicy
{
    public PolicyDecision Check(string path)
    {
        PolicyDecision builtIn = ProtectedPaths.Check(path, folders);
        if (!builtIn.Allowed) return builtIn;
        return ScanPaths.IsExcluded(path, exclusions()) || ProtectedPaths.IsUnder(path, Path.TrimEndingDirectorySeparator(applicationDirectory))
            ? new(false, "Пользовательское исключение или папка приложения.") : builtIn;
    }
}
