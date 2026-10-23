// Spike Q9: Microsoft.Windows.AppNotifications.AppNotificationManager, unpackaged + self-contained WinAppSDK.
// Register() creates per-user COM/AUMID registration; UnregisterAll() at the end removes it.

using System.Text;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

internal static class Program
{
    private const string Text = "Spacewise spike: тестовое уведомление";

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var mgr = AppNotificationManager.Default;
        bool registered = false;
        try
        {
            Console.WriteLine($"IsSupported={AppNotificationManager.IsSupported()}");
            mgr.NotificationInvoked += (_, e) => Console.WriteLine($"NotificationInvoked args={e.Argument}");
            mgr.Register();
            registered = true;
            Console.WriteLine("Register() OK");
            Console.WriteLine($"Setting={mgr.Setting}");

            var n = new AppNotificationBuilder()
                .AddArgument("action", "openReport")
                .AddText(Text)
                .BuildNotification();
            mgr.Show(n);
            Console.WriteLine($"Show() OK id={n.Id}");
            Thread.Sleep(3000);
            var all = mgr.GetAllAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine($"GetAllAsync count={all.Count}");
            mgr.RemoveAllAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("RemoveAllAsync OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EXCEPTION {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            return 1;
        }
        finally
        {
            if (registered)
            {
                try
                {
                    mgr.UnregisterAll();
                    Console.WriteLine("UnregisterAll() OK");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"UnregisterAll failed 0x{ex.HResult:X8}: {ex.Message}");
                }
            }
        }
    }
}
