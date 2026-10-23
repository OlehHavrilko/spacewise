using System.Windows;
using Microsoft.Win32;

namespace Spacewise.Desktop;
public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = this.model = model;
        Closing += (_, _) => model.ScanCommand.Cancel();
        Loaded += async (_, _) => await model.InitializeAsync();
    }
    // The folder dialog is a view concern; the result goes straight into the bound path.
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите локальную папку для анализа" };
        if (dialog.ShowDialog(this) == true) model.RootPath = dialog.FolderName;
    }
}
