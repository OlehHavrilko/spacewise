using System.Windows;
using Spacewise.Core;
using Microsoft.Win32;

namespace Spacewise.Desktop;
public sealed record FileRow(string Path, string Size, string OnDisk, string Note)
{
    public static FileRow From(FileFinding f) => new(f.Path, DisplayFormat.Size(f.LogicalBytes),
        f.AllocatedBytes is long a ? DisplayFormat.Size(a) : "—",
        f.IsHardLinked ? $"Жёсткая ссылка ({f.Links}): 0 байт выгоды" : "");
}
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
            var progress = new Progress<ScanProgress>(p => Status.Text = $"Файлов: {DisplayFormat.Count(p.Files)} · логически: {DisplayFormat.Size(p.LogicalBytes)} · пропуски: {DisplayFormat.Count(p.Skipped)} · ошибки: {DisplayFormat.Count(p.Errors)}");
            string[] excluded = Exclusions.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await new MetadataScanner().ScanAsync(new(RootPath.Text.Trim(), excluded), progress, cancellation.Token);
            session.Finish(result.State);
            Files.ItemsSource = result.LargestFiles.Select(FileRow.From).ToArray();
            Status.Text = result.State == ScanState.RootNotFound
                ? $"Анализ не выполнен: папка не найдена — {result.Root}"
                : $"{Display(result.State)} · файлов: {DisplayFormat.Count(result.Files)} · логически: {DisplayFormat.Size(result.LogicalBytes)}";
            Details.Text = Coverage(result) + "\nЭто логический объём записей, не уникальный физический объём. Место на диске показано только для крупнейших файлов. Очистка и модель отсутствуют.\n"
                + string.Join("\n", result.Diagnostics);
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
    // Three separate coverage buckets (FR-SCAN-002) instead of one "skipped" number.
    private static string Coverage(ScanResult r)
    {
        string unvisited = DisplayFormat.Count(r.UnvisitedDirectories) + " папок"
            + (r.StoppedEarly ? " и остаток после остановки" : "");
        return $"Исключено правилами: {DisplayFormat.Count(r.ExcludedByPolicy)} · Недоступно: {DisplayFormat.Count(r.Inaccessible)} · Не досканировано: {unvisited}";
    }
    private static string Display(ScanState state) => state switch
    {
        ScanState.Completed => "Проверка завершена", ScanState.Partial => "Проверка частичная",
        ScanState.Cancelled => "Остановлено; показана проверенная часть", _ => "Ошибка проверки"
    };
}
