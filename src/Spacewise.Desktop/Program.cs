using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spacewise.Catalog;
using Spacewise.Core;
using Spacewise.Data;
using Spacewise.Platform.Windows;

namespace Spacewise.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args, ContentRootPath = AppContext.BaseDirectory, DisableDefaults = true,
        });
        builder.Services.AddSingleton<IScanner, MetadataScanner>();
        builder.Services.AddSingleton<IScanService, ScanService>();
        string data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spacewise");
        builder.Services.AddSingleton<IDatabaseKeySource>(new DpapiKeySource(System.IO.Path.Combine(data, "keys")));
        builder.Services.AddKeyedSingleton("cache", (sp, _) => new DatabaseWriter(
            SqliteDatabase.Open(System.IO.Path.Combine(data, "cache.db"), Schemas.Cache, sp.GetRequiredService<IDatabaseKeySource>()).Connection));
        builder.Services.AddKeyedSingleton("journal", (sp, _) => new DatabaseWriter(
            SqliteDatabase.Open(System.IO.Path.Combine(data, "journal.db"), Schemas.Journal, sp.GetRequiredService<IDatabaseKeySource>()).Connection));
        builder.Services.AddSingleton(sp => new ScanStore(sp.GetRequiredKeyedService<DatabaseWriter>("cache")));
        builder.Services.AddSingleton(sp => new PlanStore(sp.GetRequiredKeyedService<DatabaseWriter>("journal")));
        builder.Services.AddSingleton(sp => new ExecutionStore(sp.GetRequiredKeyedService<DatabaseWriter>("journal")));
        builder.Services.AddSingleton(UserFolders.FromEnvironment());
        // Quarantine lives under the profile; a file on another volume cannot be renamed into it and is reported as failed, not deleted.
        builder.Services.AddSingleton(sp => new QuarantineFileRemover(System.IO.Path.Combine(sp.GetRequiredService<UserFolders>().Spacewise, "Quarantine")));
        builder.Services.AddSingleton<IFileRemover>(sp => sp.GetRequiredService<QuarantineFileRemover>());
        builder.Services.AddSingleton<IFileRestorer>(sp => sp.GetRequiredService<QuarantineFileRemover>());
        builder.Services.AddSingleton(sp => new PlanExecutor(sp.GetRequiredService<IFileRemover>(), sp.GetRequiredService<UserFolders>(), TimeProvider.System));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IConfirmation, MessageBoxConfirmation>();
        builder.Services.AddSingleton(_ => new CleanupPreviewer(CatalogLoader.LoadBuiltIn(), new FileSystemTree(),
            UserFolders.FromEnvironment(), TimeProvider.System, new ProcessList(), new RestartManager()));
        builder.Services.AddTransient<MainViewModel>();
        builder.Services.AddTransient<MainWindow>();
        using IHost host = builder.Build();

        // Hosted services live only while the window does: no background process after close.
        host.Start();
        try
        {
            var app = new App();
            app.InitializeComponent();
            var window = host.Services.GetRequiredService<MainWindow>();
            if (args.SequenceEqual(["--smoke-test"]))
            {
                bool ok = window.Title == "Spacewise" && window.MinWidth == 960
                    && window.DataContext is MainViewModel { ScanCommand: not null };
                window.Close();
                return ok ? 0 : 1;
            }
            return app.Run(window);
        }
        finally { host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
    }
}
