using System.Windows;
using System.Windows.Controls;

namespace Spacewise.Desktop;
public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        // Never taller/wider than the screen work area (200% DPI, small laptops).
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, Height);
        MinWidth = Math.Min(MinWidth, Width);
        DataContext = this.model = model;
        Navigation.ItemsSource = new NavigationEntry[]
        {
            new(Strings.Get("Ui.019"), new Views.AnalysisView()),
            new(Strings.Get("Ui.025"), new Views.CleanupView()),
            new(Strings.Get("Ui.037"), new Views.DisksView()),
            new(Strings.Get("Programs.Navigation"), new Views.UsageView()),
            new(Strings.Get("Ui.053"), new Views.HistoryView()),
            new(Strings.Get("Ui.065"), new Views.AssistantView()),
            new(Strings.Get("Settings.Tab"), new Views.SettingsView()),
            new(Strings.Get("Duplicates.Title"), new Views.DuplicatesView()),
        };
        Navigation.SelectedIndex = 0;
        Closing += (_, _) =>
        {
            model.ScanCommand.Cancel();
            model.PreviewCleanupCommand.Cancel();
            model.CleanCommand.Cancel();
            model.InstallModelCancelCommand.Execute(null);
            model.ExplainCleanupCancelCommand.Execute(null);
            model.ExplainSelectedFileCommand.Cancel();
            model.ExplainRemnantsCommand.Cancel();
            model.UninstallProgramCommand.Cancel();
            model.FindDuplicatesCommand.Cancel();
        };
        Loaded += async (_, _) => await model.InitializeAsync();
    }
    private sealed record NavigationEntry(string Title, UserControl Content);
}
