using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        builder.Services.AddSingleton(sp => new DatabaseWriter(
            SqliteDatabase.Open(System.IO.Path.Combine(data, "cache.db"), Schemas.Cache, sp.GetRequiredService<IDatabaseKeySource>()).Connection));
        builder.Services.AddSingleton<ScanStore>();
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
                bool ok = window.Title == "Spacewise — только анализ" && window.MinWidth == 960
                    && window.DataContext is MainViewModel { ScanCommand: not null };
                window.Close();
                return ok ? 0 : 1;
            }
            return app.Run(window);
        }
        finally { host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
    }
}
