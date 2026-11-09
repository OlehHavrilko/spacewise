using Spacewise.Core;

namespace Spacewise.Platform.Linux;

public sealed record LinuxPaths(string Home, string Cache, string Data)
{
    public static LinuxPaths FromEnvironment(bool portable = false, string? applicationDirectory = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Требуется Linux.");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string Xdg(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { } value
            && Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : fallback;
        string cache = Xdg("XDG_CACHE_HOME", Path.Combine(home, ".cache"));
        string data = portable ? Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, "Data")
            : Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(home, ".local", "share")), "spacewise");
        return new(home, cache, data);
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Contains('\0'))
            throw new ArgumentException("Нужен абсолютный Linux-путь.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool Under(string path, string root) => string.Equals(path, root, StringComparison.Ordinal)
        || path.StartsWith(root == "/" ? "/" : root + "/", StringComparison.Ordinal);
}

// Deliberately limited to explicitly selected cache roots, never the whole home directory.
public sealed class LinuxCleanupPolicy(IEnumerable<string> roots, string dataDirectory, IEnumerable<string>? exclusions = null) : ICleanupPathPolicy
{
    private readonly string[] allowed = roots.Select(LinuxPaths.Normalize).Where(p => p != "/").ToArray();
    private readonly string data = LinuxPaths.Normalize(dataDirectory);
    private readonly string[] excluded = (exclusions ?? []).Select(LinuxPaths.Normalize).ToArray();

    public PolicyDecision Check(string path)
    {
        try
        {
            string normalized = LinuxPaths.Normalize(path);
            if (normalized != path) return new(false, "Путь не канонический.");
            if (LinuxPaths.Under(path, data) || excluded.Any(p => LinuxPaths.Under(path, p)))
                return new(false, "Защищённая папка или пользовательское исключение.");
            if (new[] { "/proc", "/sys", "/dev", "/etc", "/boot", "/usr", "/bin", "/sbin", "/lib", "/lib64" }
                .Any(p => LinuxPaths.Under(path, p))) return new(false, "Системная папка.");
            return allowed.Any(p => path != p && LinuxPaths.Under(path, p))
                ? new(true, "Разрешённый кэш.") : new(false, "Вне выбранных корней очистки.");
        }
        catch (ArgumentException) { return new(false, "Недопустимый путь."); }
    }
}
