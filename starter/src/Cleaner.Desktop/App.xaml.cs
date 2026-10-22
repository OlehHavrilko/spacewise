using System.Windows;
namespace Cleaner.Desktop;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.SequenceEqual(["--smoke-test"]))
        {
            var window = new MainWindow();
            int exitCode = window.Title == "Cleaner — только анализ" && window.MinWidth == 960 ? 0 : 1;
            window.Close();
            Shutdown(exitCode);
            return;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
