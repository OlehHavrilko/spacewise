using System.Windows;
using Cleaner.Core;
using Microsoft.Win32;

namespace Cleaner.Desktop;
public partial class MainWindow : Window
{
    private CancellationTokenSource? cancellation;
    private readonly ScanSession session = new();
    public MainWindow()
    {
        InitializeComponent();
        RootPath.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Closing += (_, _) => cancellation?.Cancel();
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите локальную папку для анализа" };
        if (dialog.ShowDialog(this) == true) RootPath.Text = dialog.FolderName;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        session.Start();
        ScanButton.IsEnabled = BrowseButton.IsEnabled = RootPath.IsEnabled = Exclusions.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Busy.Visibility = Visibility.Visible;
        Files.ItemsSource = null;
        cancellation = new();
        Status.Text = "Анализ выполняется…";
        try
        {
            var progress = new Progress<ScanProgress>(p => Status.Text = $"Файлов: {p.Files:N0} · логически: {p.LogicalBytes:N0} байт · пропуски: {p.Skipped:N0} · ошибки: {p.Errors:N0}");
            string[] excluded = Exclusions.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await new MetadataScanner().ScanAsync(new(RootPath.Text.Trim(), excluded), progress, cancellation.Token);
            session.Finish(result.State);
            Files.ItemsSource = result.LargestFiles;
            Status.Text = $"{Display(result.State)} · файлов: {result.Files:N0} · логически: {result.LogicalBytes:N0} байт · пропуски: {result.Skipped:N0} · ошибки: {result.Errors:N0}";
            Details.Text = "Это логический объём записей, не уникальный физический объём. Ссылки/облачные файлы пропущены; выделенный объём неизвестен. Очистка и модель отсутствуют.\n" + string.Join("\n", result.Diagnostics);
        }
        catch (Exception ex)
        {
            session.Finish(ScanState.Failed);
            Status.Text = "Проверка не выполнена";
            Details.Text = ex.Message;
        }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            ScanButton.IsEnabled = BrowseButton.IsEnabled = RootPath.IsEnabled = Exclusions.IsEnabled = true;
            CancelButton.IsEnabled = false; Busy.Visibility = Visibility.Collapsed;
        }
    }
    private static string Display(ScanState state) => state switch
    {
        ScanState.Completed => "Проверка завершена", ScanState.Partial => "Проверка частичная",
        ScanState.Cancelled => "Остановлено; показана проверенная часть", _ => "Ошибка проверки"
    };
}
