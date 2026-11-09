using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spacewise.Catalog;
using Spacewise.Core;
using Spacewise.Data;
using Spacewise.Inference;
using Spacewise.Platform.Windows;

namespace Spacewise.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (OperationCanceledException) { return 0; }
        catch (System.Security.Cryptography.CryptographicException)
        {
            System.Windows.MessageBox.Show(Strings.Get("Portable.WrongPassword"), "Spacewise");
            return 2;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            System.Windows.MessageBox.Show(Strings.Get("Portable.StartFailed") + e.Message, "Spacewise");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        AppPaths.Configure(args);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args, ContentRootPath = AppContext.BaseDirectory, DisableDefaults = true,
        });
        builder.Services.AddSingleton<IScanner, MetadataScanner>();
        builder.Services.AddSingleton<IScanService, ScanService>();
        string data = AppPaths.Data;
        builder.Services.AddSingleton(new PersistentExclusions(System.IO.Path.Combine(data, "exclusions.json")));
        builder.Services.AddSingleton<IDatabaseKeySource>(_ => AppPaths.Portable
            ? new PortablePasswordKeySource(System.IO.Path.Combine(data, "keys"),
                Environment.GetEnvironmentVariable("SPACEWISE_PORTABLE_PASSWORD") ?? PortableKeyDialog.Password(
                    !System.IO.File.Exists(System.IO.Path.Combine(data, "keys", "portable-master.json"))))
            : new DpapiKeySource(System.IO.Path.Combine(data, "keys")));
        builder.Services.AddKeyedSingleton("cache", (sp, _) => new DatabaseWriter(
            SqliteDatabase.Open(System.IO.Path.Combine(data, "cache.db"), Schemas.Cache, sp.GetRequiredService<IDatabaseKeySource>()).Connection));
        builder.Services.AddKeyedSingleton("journal", (sp, _) => new DatabaseWriter(
            SqliteDatabase.Open(System.IO.Path.Combine(data, "journal.db"), Schemas.Journal, sp.GetRequiredService<IDatabaseKeySource>()).Connection));
        builder.Services.AddSingleton(sp => new ScanStore(sp.GetRequiredKeyedService<DatabaseWriter>("cache")));
        builder.Services.AddSingleton(sp => new FolderHistoryStore(sp.GetRequiredKeyedService<DatabaseWriter>("cache")));
        builder.Services.AddSingleton(sp => new PlanStore(sp.GetRequiredKeyedService<DatabaseWriter>("journal")));
        builder.Services.AddSingleton(sp => new ExecutionStore(sp.GetRequiredKeyedService<DatabaseWriter>("journal")));
        builder.Services.AddSingleton(UserFolders.FromEnvironment() with { Spacewise = data });
        builder.Services.AddSingleton<ICleanupPathPolicy>(sp => new WindowsCleanupPolicy(sp.GetRequiredService<UserFolders>(),
            () => sp.GetRequiredService<PersistentExclusions>().Paths, AppContext.BaseDirectory));
        builder.Services.AddSingleton(new PinnedModelInstaller(System.IO.Path.Combine(data, "models"), model: ModelProfiles.LoadOptional(System.IO.Path.Combine(data, "model.json"))));
        builder.Services.AddSingleton<LocalAssistantClient>();
        builder.Services.AddSingleton(sp => new AssistantProviderRegistry([sp.GetRequiredService<LocalAssistantClient>()]));
        builder.Services.AddSingleton(sp => new QuarantineFileRemover(System.IO.Path.Combine(sp.GetRequiredService<UserFolders>().Spacewise, "Quarantine"), requireIdentity: true));
        builder.Services.AddSingleton<IFileRemover>(_ => new HandleFileRemover(requireIdentity: true));
        builder.Services.AddSingleton<IFileRestorer>(sp => sp.GetRequiredService<QuarantineFileRemover>());
        builder.Services.AddSingleton<IQuarantinePurger>(sp => sp.GetRequiredService<QuarantineFileRemover>());
        builder.Services.AddSingleton<IVolumeSpace, DriveVolumeSpace>();
        builder.Services.AddSingleton<IRecycleBin, ShellRecycleBin>();
        builder.Services.AddSingleton<IVolumeCatalog, VolumeCatalog>();
        builder.Services.AddSingleton(sp => new VolumeHistory(sp.GetRequiredKeyedService<DatabaseWriter>("cache")));
        builder.Services.AddSingleton<QuarantineService>();
        builder.Services.AddSingleton(sp => new ExecutionRecovery(sp.GetRequiredService<ExecutionStore>(), sp.GetRequiredService<PlanStore>(),
            System.IO.Path.Combine(sp.GetRequiredService<UserFolders>().Spacewise, "Quarantine"), TimeProvider.System));
        builder.Services.AddSingleton(sp => new PlanExecutor(sp.GetRequiredService<IFileRemover>(), sp.GetRequiredService<UserFolders>(), TimeProvider.System,
            sp.GetRequiredService<ICleanupPathPolicy>()));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IConfirmation, MessageBoxConfirmation>();
        builder.Services.AddSingleton<IFileDialogs, SaveFileDialogService>();
        builder.Services.AddSingleton(sp => new CleanupPreviewer(CatalogLoader.LoadWithUserRules(System.IO.Path.Combine(data, "rules")), new FileSystemTree(),
            sp.GetRequiredService<UserFolders>(), TimeProvider.System, new ProcessList(), new RestartManager(), path => FileMetadata.TryRead(path)?.Identity,
            sp.GetRequiredService<ICleanupPathPolicy>()));
        builder.Services.AddTransient<MainViewModel>();
        builder.Services.AddTransient<MainWindow>();
        AppLog.Start();
        using IHost host = builder.Build();

        // Hosted services live only while the window does: no background process after close.
        host.Start();
        try
        {
            var app = new App();
            app.InitializeComponent();
            ThemeService.Initialize();
            if (args.SequenceEqual(["--verify-assistant-cancel"]))
                return Task.Run(() => VerifyAssistantCancellationAsync(host.Services.GetRequiredService<LocalAssistantClient>())).GetAwaiter().GetResult();
            if (args.SequenceEqual(["--verify-assistant"]))
                return Task.Run(() => VerifyAssistantResponseAsync(host.Services.GetRequiredService<LocalAssistantClient>())).GetAwaiter().GetResult();
            var window = host.Services.GetRequiredService<MainWindow>();
            app.MainWindow = window;
            if (args.Length is 1 or 2 && args[0] == "--verify-layout")
                return LayoutVerifier.Run(window, args.Length == 2 ? args[1] : null);
            if (args.SequenceEqual(["--smoke-test"]))
            {
                bool ok = window.Title == "Spacewise" && window.Width <= System.Windows.SystemParameters.WorkArea.Width
                    && window.DataContext is MainViewModel { ScanCommand: not null };
                window.Close();
                return ok ? 0 : 1;
            }
            return app.Run(window);
        }
        finally
        {
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            AppLog.Stop();
        }
    }

    private static async Task<int> VerifyAssistantCancellationAsync(LocalAssistantClient client)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await client.ExplainAsync("Explain consequences.", [new("F001", "Cache", "regenerated", "Recreated.", 1, 100)], cancellation.Token);
            Console.Error.WriteLine("FAIL: expected cancellation.");
            return 1;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine("PASS: assistant client cancelled and awaited worker shutdown.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task<int> VerifyAssistantResponseAsync(LocalAssistantClient client)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            AssistantResponse response = await client.ExplainAsync(Strings.Get("AI.DefaultQuestion"),
                [new("F001", Strings.Get("Ui.025"), "regenerated", Strings.Get("Cleanup.Regenerated"), 1, 100)], cancellation.Token);
            Console.WriteLine("PASS: desktop assistant client round trip.");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(response));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
