using System.Windows;
namespace Spacewise.Desktop;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.SequenceEqual(["--smoke-test"]))
        {
            var window = new MainWindow();
            int exitCode = window.Title == "Spacewise — только анализ" && window.MinWidth == 960 ? 0 : 1;
            window.Close();
            Shutdown(exitCode);
            return;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
