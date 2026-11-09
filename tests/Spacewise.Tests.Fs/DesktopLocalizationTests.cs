using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Spacewise.Desktop;

namespace Spacewise.Tests.Fs;

[Collection("Desktop app")]
public sealed class DesktopLocalizationTests
{
    [Fact]
    public void Compiled_Wpf_resources_preserve_whitespace_formats_culture_and_literal_arguments()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new App();
            try
            {
                app.InitializeComponent();
                Type strings = typeof(App).Assembly.GetType("Spacewise.Desktop.Strings", throwOnError: true)!;
                MethodInfo format = strings.GetMethod("Format")!;
                string Format(string key, params object?[] args) => (string)format.Invoke(null, [key, args])!;
                Assert.Equal("запущено, закройте: ", app.FindResource("Cleanup.CloseRunning"));
                Assert.StartsWith("\n", (string)app.FindResource("Scan.VolumeExplanation"));
                Assert.EndsWith("\n\n", (string)app.FindResource("Cleanup.ConfirmSkipped"));
                var date = new DateTime(2026, 10, 30);
                foreach (string culture in new[] { "ru-RU", "en-US" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    Assert.Equal($"В карантине: 2 файлов, логически 1 Б; записанный срок хранения до {date:d}. Файлы сохраняются до ручного возврата или удаления.",
                        Format("Quarantine.Summary", "2", "1 Б", date));
                    Assert.Equal($"Заполнится примерно {date:d} (через 3 дн.; ", Format("Forecast.Expected", date, "3"));
                    Assert.Equal($"{12.3:0} %", Format("Disk.UsedPercent", 12.3));
                    Assert.Equal("Анализ не выполнен: папка не найдена — C:\\{folder}\\{0:d}",
                        Format("Scan.RootNotFound", "C:\\{folder}\\{0:d}"));
                }
                Assert.Throws<TargetInvocationException>(() => Format("Missing.Resource"));
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Localization check timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
