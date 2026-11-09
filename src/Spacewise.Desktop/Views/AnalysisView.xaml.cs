using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Spacewise.Desktop.Views;

public partial class AnalysisView : UserControl
{
    public AnalysisView() => InitializeComponent();
    private void Folder_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainViewModel model && e.NewValue is FolderNode node) model.SelectedFolder = node;
    }
    // The folder dialog is a view concern; the result goes straight into the bound path.
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Strings.Get("Dialog.SelectFolder") };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && DataContext is MainViewModel model) model.RootPath = dialog.FolderName;
    }

}
