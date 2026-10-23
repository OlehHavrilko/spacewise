// Spike Q9: raw Windows.UI.Notifications (WinRT projection from TFM net10.0-windows10.0.19041.0).
// Modes:
//   noreg  - AUMID set only for the process (SetCurrentProcessExplicitAppUserModelID), nothing registered.
//   hkcu   - temporary HKCU\Software\Classes\AppUserModelId\<AUMID> (DisplayName), removed at the end.
// The toast is removed from the Action Center history at the end of every mode.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

internal static class Program
{
    private const string Aumid = "Spacewise.Spike.Toast";
    private const string Text = "Spacewise spike: тестовое уведомление";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string mode = args.Length > 0 ? args[0] : "noreg";
        Console.WriteLine($"mode={mode} aumid={Aumid}");
        Console.WriteLine($"SetCurrentProcessExplicitAppUserModelID hr=0x{SetCurrentProcessExplicitAppUserModelID(Aumid):X8}");

        string keyPath = @"Software\Classes\AppUserModelId\" + Aumid;
        bool createdKey = false;
        try
        {
            if (mode == "hkcu")
            {
                if (Registry.CurrentUser.OpenSubKey(keyPath) is not null)
                {
                    Console.WriteLine("HKCU AUMID key already exists; refusing to touch it.");
                    return 3;
                }

                using var k = Registry.CurrentUser.CreateSubKey(keyPath);
                k.SetValue("DisplayName", "Spacewise spike");
                createdKey = true;
                Console.WriteLine($"created HKCU\\{keyPath} (DisplayName)");
            }

            return Show();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EXCEPTION {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            return 1;
        }
        finally
        {
            try
            {
                ToastNotificationManager.History.Clear(Aumid);
                Console.WriteLine("history cleared");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"history clear failed 0x{ex.HResult:X8}");
            }

            if (createdKey)
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
                Console.WriteLine($"deleted HKCU\\{keyPath}; exists now: {Registry.CurrentUser.OpenSubKey(keyPath) is not null}");
            }
        }
    }

    private static int Show()
    {
        var notifier = ToastNotificationManager.CreateToastNotifier(Aumid);
        Console.WriteLine($"notifier.Setting={notifier.Setting}");

        var xml = new XmlDocument();
        xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text></text></binding></visual></toast>");
        xml.GetElementsByTagName("text")[0].AppendChild(xml.CreateTextNode(Text));
        var toast = new ToastNotification(xml) { Tag = "spike", Group = "spike" };

        using var done = new ManualResetEventSlim();
        string outcome = "no event within 8 s (toast stayed on screen or was queued)";
        toast.Failed += (_, e) => { outcome = $"Failed 0x{e.ErrorCode.HResult:X8}"; done.Set(); };
        toast.Dismissed += (_, e) => { outcome = $"Dismissed reason={e.Reason}"; done.Set(); };
        toast.Activated += (_, _) => { outcome = "Activated (clicked)"; done.Set(); };

        notifier.Show(toast);
        Console.WriteLine("Show() returned without exception");
        Thread.Sleep(1500);
        var history = ToastNotificationManager.History.GetHistory(Aumid);
        Console.WriteLine($"history count after 1.5 s = {history.Count}");
        done.Wait(TimeSpan.FromSeconds(8));
        Console.WriteLine($"outcome: {outcome}");
        return 0;
    }
}
