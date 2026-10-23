using Serilog;

namespace Spacewise.Desktop;

// FR-DATA-004: logs in %LOCALAPPDATA%\Spacewise\logs, daily files, 7 files of at most 7 MB (about 7 days or 49 MB).
// Information and above never contain file paths or exception messages (those often embed paths): only counts,
// states and exception type names. The full exception goes to Debug, which is off unless SPACEWISE_LOG=debug.
public static class AppLog
{
    public static string Directory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spacewise", "logs");

    public static void Start()
    {
        bool debug = string.Equals(Environment.GetEnvironmentVariable("SPACEWISE_LOG"), "debug", StringComparison.OrdinalIgnoreCase);
        var config = new LoggerConfiguration()
            .WriteTo.File(System.IO.Path.Combine(Directory, "spacewise-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7, fileSizeLimitBytes: 7 * 1024 * 1024, rollOnFileSizeLimit: true, shared: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        Log.Logger = (debug ? config.MinimumLevel.Debug() : config.MinimumLevel.Information()).CreateLogger();
        Log.Information("Spacewise started, version {Version}", typeof(AppLog).Assembly.GetName().Version);
    }

    public static void Info(string template, params object?[] values) => Log.Information(template, values);

    public static void Failure(string what, Exception ex)
    {
        Log.Error("{What} failed: {Type} (HResult 0x{HResult:X8})", what, ex.GetType().Name, ex.HResult);
        Log.Debug(ex, "{What} details", what);
    }

    public static void Stop() => Log.CloseAndFlush();
}
