using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Spacewise.Core;
using Spacewise.Catalog;

namespace Spacewise.Desktop;

// Explicit diagnostic mode: exercises DIP layouts without changing Windows DPI or accessibility settings.
internal static class LayoutVerifier
{
    public static int Run(MainWindow window, string? outputDirectory)
    {
        try
        {
            var model = (MainViewModel)window.DataContext;
            model.ShowWelcome = false;
            var result = new ScanResult(Guid.NewGuid(), @"C:\Fixture", ScanState.Completed, 3, 1000, 0, 0,
                [new(@"C:\Fixture\Large\a.bin", 700), new(@"C:\Fixture\Small\b.bin", 200), new(@"C:\Fixture\root.bin", 100)], [])
            { Folders = [new(@"C:\Fixture\Large", 1, 700), new(@"C:\Fixture\Small", 1, 200)] };
            model.RootPath = result.Root;
            model.Files = result.LargestFiles.Select(FileRow.From).ToArray();
            var tree = FolderNode.Build(result);
            model.FolderRoots = [tree];
            model.MapFolders = tree.Children;
            model.SelectedFolder = tree;
            model.AnalysisCoverage = Strings.Format("Analysis.Coverage", "3", "3");
            CleanupRule rule = CatalogLoader.LoadBuiltIn().Rules.First(r => r.Action == "delete" && !r.RequiresAdmin);
            model.Cleanup = [new CleanupRow(new RulePreview(rule, [new(@"C:\Fixture\cache.bin", 100, DateTime.UtcNow)], 0, 0, 0))];
            model.CleanupReport = Strings.Get("Cleanup.ReportNote");
            model.ProgramRemnants = [.. model.Files.Select(file => new ProgramRemnantRow(new(file.Path, 100), null))];
            model.ProgramActionStatus = Strings.Format("Programs.Remnants", "3", "3");
            var demo = new InstalledProgram("fixture", "Example Editor", "Spacewise QA", @"C:\Fixture\Editor", ProgramKind.Win32, 230L * 1024 * 1024, null);
            model.UsageRows = [new(new ProgramUsage(demo, ObservationStatus.UnknownHistory, EvidenceLevel.Unknown, null, 0, "", 0), DateTime.UtcNow)];
            var root = (FrameworkElement)window.Content;
            var navigation = (ListBox)window.FindName("Navigation");
            int cases = 0;
            foreach (string palette in new[] { "Light", "Dark", "System" })
            {
                Application.Current.ThemeMode = ThemeMode.None;
                Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary
                { Source = new Uri($"/Spacewise.Desktop;component/Resources/Theme.{palette}.xaml", UriKind.Relative) };
                foreach (double scale in new[] { 1.25, 1.5, 2.0 })
                foreach (var pixels in new[] { new Size(1920, 1080), new Size(1280, 720) })
                {
                    var size = new Size(pixels.Width / scale, pixels.Height / scale);
                    for (int i = 0; i < navigation.Items.Count; i++)
                    {
                        navigation.SelectedIndex = i;
                        if (navigation.SelectedValue is Views.UsageView programs && programs.FindName("RemnantsPanel") is Expander panel)
                            panel.IsExpanded = true;
                        root.Measure(size);
                        root.Arrange(new Rect(size));
                        root.UpdateLayout();
                        var item = (ListBoxItem)navigation.ItemContainerGenerator.ContainerFromIndex(i);
                        if (item is null || item.ActualWidth <= 0 || item.ActualWidth > navigation.ActualWidth + 1)
                            throw new InvalidOperationException($"Navigation clipping: {palette}, {scale}, {pixels}, screen {i}");
                        var label = Descendants<TextBlock>(item).Single();
                        if (label.DesiredSize.Height > label.ActualHeight + 1)
                            throw new InvalidOperationException($"Navigation text clipping: screen {i}");
                        var screen = navigation.SelectedValue as FrameworkElement;
                        if (screen is null || screen.ActualWidth <= 0 || screen.ActualHeight < 439)
                            throw new InvalidOperationException($"Screen has no bounded viewport: screen {i}");
                        foreach (DataGrid grid in Descendants<DataGrid>(screen))
                        {
                            if (!grid.EnableRowVirtualization || !grid.EnableColumnVirtualization
                                || !VirtualizingPanel.GetIsVirtualizing(grid)
                                || VirtualizingPanel.GetVirtualizationMode(grid) != VirtualizationMode.Recycling)
                                throw new InvalidOperationException("Grid virtualization is disabled.");
                            if (grid.ActualHeight < 43) throw new InvalidOperationException($"Grid rows are clipped: screen {i}");
                        }
                        cases++;
                        if (outputDirectory is not null && pixels.Width == 1920 && (i == 0 || i == 3 && scale == 1.25))
                        {
                            Directory.CreateDirectory(outputDirectory);
                            var bitmap = new RenderTargetBitmap((int)pixels.Width, (int)pixels.Height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                            bitmap.Render(root);
                            var png = new PngBitmapEncoder();
                            png.Frames.Add(BitmapFrame.Create(bitmap));
                            string page = i == 0 ? "layout" : "programs";
                            using var output = File.Create(Path.Combine(outputDirectory, $"{page}-{palette}-{scale * 100:0}.png"));
                            png.Save(output);
                        }
                    }
                }
            }
            // A file selection must locate its folder and map tile; selecting a map tile filters the table.
            model.SelectedFile = model.Files[0];
            if (model.SelectedFolder?.Path != @"C:\Fixture\Large" || model.SelectedMapFolder?.Path != @"C:\Fixture\Large")
                throw new InvalidOperationException("File/tree/map selection is not synchronized.");
            model.SelectedMapFolder = tree.Children.Single(n => n.Path.EndsWith("Small", StringComparison.Ordinal));
            if (model.VisibleFiles.Count != 1 || model.VisibleFiles[0].Path != @"C:\Fixture\Small\b.bin")
                throw new InvalidOperationException("Map/table filtering is not synchronized.");
            VerifyProgress();
            Console.WriteLine($"PASS: {cases} layout cases; 125/150/200% DIP constraints; Light/Dark/System; selection synchronization.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { window.Close(); }
    }

    private static void VerifyProgress()
    {
        var reports = new List<(DateTime Time, int Value)>();
        using var progress = new UiProgress<int>(value => reports.Add((DateTime.UtcNow, value)));
        Task producer = Task.Run(() => { for (int i = 0; i < 1_000_000; i++) progress.Report(i); });
        var frame = new DispatcherFrame();
        DateTime started = DateTime.UtcNow;
        var stop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        stop.Tick += (_, _) =>
        {
            if (producer.IsCompleted && reports.LastOrDefault().Value == 999_999 || DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
                frame.Continue = false;
        };
        stop.Start();
        Dispatcher.PushFrame(frame);
        stop.Stop();
        producer.GetAwaiter().GetResult();
        if (reports.Count == 0 || reports[^1].Value != 999_999)
            throw new InvalidOperationException("Coalesced progress lost the latest value.");
        for (int i = 1; i < reports.Count; i++)
            if (reports[i].Time - reports[i - 1].Time < TimeSpan.FromMilliseconds(240))
                throw new InvalidOperationException("Progress exceeded 4 Hz.");
        progress.Dispose();
        int count = reports.Count;
        progress.Report(-1);
        var disposedFrame = new DispatcherFrame();
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        deadline.Tick += (_, _) => disposedFrame.Continue = false;
        deadline.Start();
        Dispatcher.PushFrame(disposedFrame);
        deadline.Stop();
        if (reports.Count != count) throw new InvalidOperationException("Disposed progress overwrote a final status.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
