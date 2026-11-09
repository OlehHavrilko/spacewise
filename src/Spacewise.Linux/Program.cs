using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Spacewise.Platform.Linux;

namespace Spacewise.Linux;

public static class Program
{
    internal static LinuxPaths Paths { get; private set; } = null!;
    internal static bool SmokeTest { get; private set; }
    internal static string? Screenshot { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux()) { Console.Error.WriteLine("Эта сборка предназначена для Linux."); return 2; }
        try
        {
            Paths = LinuxPaths.FromEnvironment(args.Contains("--portable") || File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag")));
            int benchmark = Array.IndexOf(args, "--benchmark");
            if (benchmark >= 0)
            {
                if (benchmark + 1 >= args.Length) { Console.Error.WriteLine("Укажите корень после --benchmark."); return 2; }
                using var cancelled = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled.Cancel(); };
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = new LinuxScanner().ScanAsync(new(args[benchmark + 1], [Paths.Data]), null, cancelled.Token).GetAwaiter().GetResult();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { State = result.State.ToString(), result.Files, result.LogicalBytes, result.Skipped, result.Errors,
                    ElapsedSeconds = watch.Elapsed.TotalSeconds, ManagedBytes = GC.GetTotalMemory(false), result.StoppedEarly }));
                return result.State == Spacewise.Core.ScanState.Completed ? 0 : 1;
            }
            SmokeTest = args.Contains("--smoke-test");
            int screenshot = Array.IndexOf(args, "--screenshot");
            if (screenshot >= 0)
            {
                if (screenshot + 1 >= args.Length) { Console.Error.WriteLine("Укажите путь после --screenshot."); return 2; }
                Screenshot = args[screenshot + 1];
            }
            return AppBuilder.Configure<LinuxApplication>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine(e.Message); return 1; }
    }
}

public sealed class LinuxApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Program.Paths);
            if (Program.SmokeTest)
                desktop.MainWindow.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (Program.Screenshot is { } path)
                        {
                            var window = desktop.MainWindow;
                            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
                            bitmap.Render(window);
                            using var output = new FileStream(path, FileMode.CreateNew);
                            bitmap.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                        }
                        Console.WriteLine("Linux GUI smoke: passed");
                        desktop.Shutdown(0);
                    }
                    catch (Exception e) { Console.Error.WriteLine(e); desktop.Shutdown(1); }
                }, Avalonia.Threading.DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
