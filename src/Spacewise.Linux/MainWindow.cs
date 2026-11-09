using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Spacewise.Core;
using Spacewise.Platform.Linux;

namespace Spacewise.Linux;

public sealed class MainWindow : Window
{
    private readonly LinuxPaths paths;
    private readonly LinuxAppStore store;
    private readonly TextBox root = new() { PlaceholderText = "/home/user", MinWidth = 300 };
    private readonly TextBox exclusions = new() { AcceptsReturn = true, MinHeight = 110, PlaceholderText = "Абсолютные пути, по одному на строку" };
    private readonly TextBlock status = new() { Text = "Готово", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox files = new() { MinHeight = 160 };
    private readonly ListBox folders = new() { MinHeight = 100 };
    private readonly ListBox held = new() { MinHeight = 200 };
    private readonly ListBox history = new() { MinHeight = 200 };
    private readonly StackPanel categories = new() { Spacing = 12 };
    private readonly SpaceMap map = new() { Height = 200, IsVisible = false };
    private readonly List<Button> actions = [];
    private readonly List<(CheckBox Check, LinuxPreview Preview)> previews = [];
    private readonly Button cancel = new() { Content = "Отменить", IsEnabled = false };
    private CancellationTokenSource? operation;
    private IReadOnlyList<LinuxHeldFile> heldFiles = [];
    private ScanResult? lastScan;
    private readonly CheckBox reportPaths = new() { Content = "Включить полные пути в HTML-отчёт", IsChecked = false };
    private readonly ListBox duplicates = new() { MinHeight = 240 };
    private readonly ComboBox trendFolder = new() { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly FolderTrendChart trend = new() { Height = 180 };

    public MainWindow(LinuxPaths paths)
    {
        this.paths = paths;
        store = new(paths.Data);
        Title = "Spacewise — Linux";
        Width = 1060; Height = 820; MinWidth = 720; MinHeight = 560;
        root.Text = paths.Home;
        var tabs = new TabControl
        {
            ItemsSource = new TabItem[]
            {
                Tab("Анализ", new StackPanel { Spacing = 12, Children =
                {
                    new TextBlock { Text = "Папка для анализа", FontWeight = FontWeight.SemiBold }, root,
                    Button("Сканировать", ScanAsync), reportPaths, Button("Сохранить HTML-отчёт…", ExportAsync), summary, map,
                    new TextBlock { Text = "Папки (вложенные итоги не складываются)" }, folders,
                    new TextBlock { Text = "Крупнейшие 100 файлов" }, files,
                }}),
                Tab("Очистка", new StackPanel { Spacing = 12, Children =
                {
                    new TextBlock { Text = "Выберите категории после предпросмотра. Закройте приложения, использующие кэш.", TextWrapping = TextWrapping.Wrap },
                    Button("Найти кэши", PreviewAsync), categories,
                    Button("Переместить выбранное в карантин…", CleanAsync),
                    new TextBlock { Text = "Карантин сохраняет файлы для восстановления. Место освобождается после окончательного удаления.", TextWrapping = TextWrapping.Wrap },
                }}),
                Tab("Карантин", new StackPanel { Spacing = 12, Children =
                {
                    Button("Обновить список", RefreshHeldAsync), held,
                    Button("Восстановить выбранный файл…", RestoreAsync),
                    Button("Окончательно удалить выбранный файл…", PurgeAsync),
                }}),
                Tab("Дубликаты", new StackPanel { Spacing = 12, Children =
                {
                    new TextBlock { Text = "Папка и исключения берутся из анализа. Чтение содержимого: до 100 000 кандидатов и 16 ГиБ. Ссылки и облачные/сетевые ФС пропускаются. Результаты служат для проверки, автоматического удаления нет.", TextWrapping = TextWrapping.Wrap },
                    Button("Найти дубликаты (BLAKE3)", FindDuplicatesAsync), duplicates,
                }}),
                Tab("История", new StackPanel { Spacing = 12, Children =
                {
                    new TextBlock { Text = "Рост папок за 31 день: только полные сканы, до 64 папок на корень, 8 корней. Первые точки появятся после сканирования.", TextWrapping = TextWrapping.Wrap },
                    trendFolder, trend, history,
                }}),
                Tab("Настройки и помощь", new StackPanel { Spacing = 12, Children =
                {
                    new TextBlock { Text = "Никогда не сканировать и не очищать:", FontWeight = FontWeight.SemiBold }, exclusions,
                    Button("Сохранить исключения", SaveSettingsAsync),
                    new TextBlock { Text = "Данные: " + paths.Data, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Данные защищены правами пользователя Unix, без шифрования. Для переноса рядом с приложением создайте portable.flag или запустите с --portable.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Что удаляется? Только файлы известных кэшей из выбранных категорий. Возраст файла сам по себе не делает его безопасным для удаления. Символические и жёсткие ссылки не очищаются.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Можно отменить? Пока файл находится в карантине, его можно восстановить. Окончательное удаление необратимо. Существующий файл при восстановлении не заменяется.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "ИИ, облако, телеметрия и фоновый мониторинг в этой версии не используются.", TextWrapping = TextWrapping.Wrap },
                }}),
            },
        };
        cancel.Click += (_, _) => operation?.Cancel();
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(18), Children = { status, cancel } };
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel { Children = { footer, tabs } };
        Opened += (_, _) =>
        {
            try
            {
                exclusions.Text = string.Join(Environment.NewLine, store.Settings().Exclusions);
                RefreshHistory();
            }
            catch (Exception e) { status.Text = "Не удалось прочитать настройки: " + e.Message; }
        };
        Closing += (_, _) => operation?.Cancel();
        exclusions.TextChanged += (_, _) => { previews.Clear(); categories.Children.Clear(); };
        trendFolder.SelectionChanged += (_, _) =>
        {
            if (trendFolder.SelectedItem is string path)
                trend.SetPoints(store.FolderHistory().SelectMany(s => s.Folders.Where(f => f.Path == path).Select(f => new FolderTrendPoint(s.Utc, f.LogicalBytes)))
                    .GroupBy(p => p.TakenUtc.Date).Select(g => g.MaxBy(p => p.TakenUtc)!).OrderBy(p => p.TakenUtc).ToArray());
        };
    }

    private static TabItem Tab(string title, Control content) => new()
    {
        Header = title,
        FontSize = 14,
        Content = new ScrollViewer { Content = content, Margin = new Thickness(18), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
    };

    private Button Button(string title, Func<CancellationToken, Task> action)
    {
        var button = new Button { Content = title };
        actions.Add(button);
        button.Click += async (_, _) =>
        {
            if (operation is not null) return;
            operation = new CancellationTokenSource();
            foreach (Button item in actions) item.IsEnabled = false;
            cancel.IsEnabled = true; root.IsEnabled = false; exclusions.IsEnabled = false;
            try { await action(operation.Token); }
            catch (OperationCanceledException) { status.Text = "Операция отменена."; }
            catch (Exception e) { status.Text = "Операция не выполнена: " + e.Message; }
            finally
            {
                operation.Dispose(); operation = null;
                foreach (Button item in actions) item.IsEnabled = true;
                cancel.IsEnabled = false; root.IsEnabled = true; exclusions.IsEnabled = true;
            }
        };
        return button;
    }

    private string[] Exclusions() => (exclusions.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(LinuxPaths.Normalize).Distinct(StringComparer.Ordinal).ToArray();

    private async Task ScanAsync(CancellationToken cancellation)
    {
        string[] excluded = Exclusions();
        store.SaveSettings(new(excluded));
        status.Text = "Сканирование…";
        var progress = new Progress<ScanProgress>(p => status.Text = $"Файлов: {p.Files:N0}; {DisplayFormat.Size(p.LogicalBytes)}; ошибок: {p.Errors:N0}");
        ScanResult result = await new LinuxScanner().ScanAsync(new(root.Text ?? "", [.. excluded, paths.Data]), progress, cancellation);
        lastScan = result;
        summary.Text = $"{result.State}: {result.Files:N0} файлов; логический размер {DisplayFormat.Size(result.LogicalBytes)}. Пропущено: {result.Skipped:N0}; ошибок: {result.Errors:N0}.";
        if (result.FolderAggregationTruncated) summary.Text += " Агрегаты папок ограничены.";
        files.ItemsSource = result.LargestFiles.Select(f => $"{DisplayFormat.Size(f.LogicalBytes)} | на диске {DisplayFormat.Size(f.AllocatedBytes ?? 0)} | {f.Path}").ToArray();
        folders.ItemsSource = result.Folders.Take(100).Select(f => $"{DisplayFormat.Size(f.LogicalBytes)} | {f.Path}").ToArray();
        string scanRoot = LinuxPaths.Normalize(result.Root);
        map.SetFolders(result.Folders.Where(f => Path.GetDirectoryName(f.Path) == scanRoot).Take(30).ToArray());
        map.IsVisible = result.Folders.Count > 0;
        store.SaveScan(result);
        RefreshHistory();
        status.Text = result.State == ScanState.Completed ? "Сканирование завершено." : "Сканирование неполное: " + result.State;
    }

    private async Task PreviewAsync(CancellationToken cancellation)
    {
        string[] excluded = Exclusions();
        store.SaveSettings(new(excluded));
        status.Text = "Предпросмотр кэшей…";
        previews.Clear(); categories.Children.Clear();
        foreach (LinuxPreview preview in await new LinuxCleanupPreviewer(paths, TimeProvider.System).PreviewAsync(excluded, cancellation))
        {
            var check = new CheckBox { Content = $"{preview.Rule.Title}: {preview.Items.Count:N0} файлов, {DisplayFormat.Size(preview.Items.Sum(i => i.ExpectedSize))}",
                IsChecked = false, IsEnabled = preview.Complete && preview.Items.Count > 0 };
            previews.Add((check, preview));
            categories.Children.Add(new StackPanel { Spacing = 3, Children =
            {
                check,
                new TextBlock { Text = preview.Rule.Consequences + (preview.Complete ? "" : " Категория недоступна: обход неполный."), TextWrapping = TextWrapping.Wrap },
            }});
        }
        status.Text = previews.Count == 0 ? "Поддерживаемые кэши не найдены." : "Предпросмотр готов. Категории не выбраны автоматически.";
    }

    private LinuxQuarantine Quarantine(ICleanupPathPolicy? policy = null) => new(paths.Data,
        policy ?? new LinuxCleanupPolicy([], paths.Data, Exclusions()));

    private async Task CleanAsync(CancellationToken cancellation)
    {
        LinuxPreview[] selected = previews.Where(p => p.Check.IsChecked == true && p.Preview.Complete).Select(p => p.Preview).ToArray();
        if (selected.Length == 0) { status.Text = "Выберите категорию после предпросмотра."; return; }
        var policy = new LinuxCleanupPolicy(selected.Select(p => Path.Combine(paths.Cache, p.Rule.Directory)), paths.Data, Exclusions());
        CleanupPlan plan = CleanupPlan.Create(selected.SelectMany(p => p.Items), DateTime.UtcNow, TimeSpan.FromMinutes(10), "linux-ui");
        if (!await Confirm("Переместить в карантин?", $"Файлов: {plan.Items.Count:N0}. Размер: {DisplayFormat.Size(plan.ExpectedBytes)}.\nМесто пока не освободится. Файлы можно будет восстановить.")) return;
        cancellation.ThrowIfCancellationRequested();
        status.Text = "Перемещение в карантин…";
        ExecutionReport report = await Task.Run(() => new PlanExecutor(Quarantine(policy), policy, TimeProvider.System).Execute(plan, cancellation), CancellationToken.None);
        previews.Clear(); categories.Children.Clear();
        await RefreshHeldAsync(CancellationToken.None);
        status.Text = $"В карантине: {report.Removed:N0}; пропущено или ошибок: {report.Outcomes.Count - report.Removed:N0}." + (report.Cancelled ? " Операция отменена." : "");
    }

    private async Task RefreshHeldAsync(CancellationToken cancellation)
    {
        LinuxQuarantine quarantine = Quarantine();
        heldFiles = await Task.Run(quarantine.List, cancellation);
        held.ItemsSource = heldFiles.Select(f => $"{DisplayFormat.Size(f.Size)} | {f.OriginalPath} | {f.CreatedUtc.ToLocalTime():g}").ToArray();
        status.Text = $"В карантине: {heldFiles.Count:N0} файлов.";
    }

    private async Task RestoreAsync(CancellationToken cancellation)
    {
        int index = held.SelectedIndex;
        if (index < 0 || index >= heldFiles.Count) { status.Text = "Выберите файл."; return; }
        LinuxHeldFile file = heldFiles[index];
        if (!await Confirm("Восстановить файл?", file.OriginalPath + "\nСуществующий файл не будет заменён.")) return;
        cancellation.ThrowIfCancellationRequested();
        LinuxQuarantine quarantine = Quarantine();
        RestoreResult result = await Task.Run(() => quarantine.Restore(file.Id), cancellation);
        await RefreshHeldAsync(CancellationToken.None);
        status.Text = result.Restored ? "Файл восстановлен." : $"Восстановление не выполнено. Код: {result.Code}; несовпадение содержимого: {result.HashMismatch}.";
    }

    private async Task PurgeAsync(CancellationToken cancellation)
    {
        int index = held.SelectedIndex;
        if (index < 0 || index >= heldFiles.Count) { status.Text = "Выберите файл."; return; }
        LinuxHeldFile file = heldFiles[index];
        if (!await Confirm("Удалить безвозвратно?", file.OriginalPath + "\nПосле удаления из карантина восстановление в Spacewise невозможно.")) return;
        cancellation.ThrowIfCancellationRequested();
        LinuxQuarantine quarantine = Quarantine();
        bool purged = await Task.Run(() => quarantine.Purge(file.Id), cancellation);
        await RefreshHeldAsync(CancellationToken.None);
        status.Text = purged ? "Файл окончательно удалён." : "Файл не удалён: метаданные или содержимое изменились.";
    }

    private Task SaveSettingsAsync(CancellationToken cancellation)
    { store.SaveSettings(new(Exclusions())); status.Text = "Исключения сохранены."; return Task.CompletedTask; }

    private async Task ExportAsync(CancellationToken cancellation)
    {
        if (lastScan is null) { status.Text = "Сначала выполните сканирование."; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "HTML-отчёт Spacewise", SuggestedFileName = "spacewise-scan.html", DefaultExtension = "html",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("HTML") { Patterns = ["*.html"] }],
        });
        if (file is null) return;
        cancellation.ThrowIfCancellationRequested();
        await using Stream output = await file.OpenWriteAsync();
        output.SetLength(0);
        byte[] html = System.Text.Encoding.UTF8.GetBytes(ScanHtmlReport.Build(lastScan, DateTime.UtcNow, reportPaths.IsChecked == true));
        await output.WriteAsync(html, cancellation);
        status.Text = "HTML-отчёт сохранён.";
    }

    private async Task FindDuplicatesAsync(CancellationToken cancellation)
    {
        string[] excluded = Exclusions();
        store.SaveSettings(new(excluded));
        status.Text = "Сравнение размеров и хеширование содержимого BLAKE3…";
        DuplicateReport report = await new DuplicateFinder(new LinuxDuplicateSource()).FindAsync(root.Text ?? "", [.. excluded, paths.Data], cancellation);
        duplicates.ItemsSource = report.Groups.Select(g => $"{DisplayFormat.Size(g.Files[0].Size)} × {g.Files.Count}\n" + string.Join("\n", g.Files.Select(f => f.Path))).ToArray();
        status.Text = $"Групп дубликатов: {report.Groups.Count}; кандидатов: {report.Candidates:N0}; прочитано {DisplayFormat.Size(report.BytesRead)}; пропущено при сравнении: {report.Skipped}." + (report.Limited ? " Достигнут лимит." : " Недоступные файлы и ссылки не сравнивались.");
    }

    private void RefreshHistory()
    {
        history.ItemsSource = store.History().Reverse().Select(s => $"{s.Utc.ToLocalTime():g} | {s.State} | {s.Files:N0} файлов | {DisplayFormat.Size(s.Bytes)} | {s.Root}").ToArray();
        trendFolder.ItemsSource = store.FolderHistory().SelectMany(s => s.Folders.Select(f => f.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        trendFolder.SelectedIndex = 0;
    }

    private async Task<bool> Confirm(string title, string message)
    {
        var dialog = new Window { Title = title, Width = 540, Height = 260, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var yes = new Button { Content = "Подтвердить" };
        var no = new Button { Content = "Отмена" };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children =
        {
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { no, yes } },
        }};
        return await dialog.ShowDialog<bool>(this);
    }
}
