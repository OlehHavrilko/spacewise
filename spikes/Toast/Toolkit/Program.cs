// Spike Q9: Microsoft.Toolkit.Uwp.Notifications 7.1.3. For unpackaged apps ToastNotificationManagerCompat
// auto-registers an AUMID + COM activator under HKCU; Uninstall() at the end removes it.

using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 1 && args[0] == "--uninstall")
        {
            ToastNotificationManagerCompat.Uninstall();
            Console.WriteLine("Uninstall() done");
            return 0;
        }

        bool keep = args.Length == 1 && args[0] == "--keep"; // leave registration so it can be inspected
        try
        {
            ToastNotificationManagerCompat.OnActivated += e => Console.WriteLine($"OnActivated args={e.Argument}");
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            Console.WriteLine($"notifier.Setting={notifier.Setting}");
            new ToastContentBuilder()
                .AddArgument("action", "openReport")
                .AddText("Spacewise spike: тестовое уведомление")
                .Show();
            Console.WriteLine("Show() OK");
            Thread.Sleep(3000);
            Console.WriteLine($"history count={ToastNotificationManagerCompat.History.GetHistory().Count}");
            ToastNotificationManagerCompat.History.Clear();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EXCEPTION {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            return 1;
        }
        finally
        {
            if (!keep)
            {
                ToastNotificationManagerCompat.Uninstall();
                Console.WriteLine("Uninstall() done");
            }
        }
    }
}
