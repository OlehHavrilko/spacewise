using System.Diagnostics;
using System.Windows.Automation;

namespace Spacewise.Tests.Fs;

// A11 (automatable part): every control a user can operate has an accessible name and can take keyboard focus,
// so Narrator announces it and the keyboard reaches it. Contrast, 200 % DPI and Narrator speech stay a manual pass.
[Collection("Desktop app")]
public class AccessibilityTests
{
    [Fact]
    public void Interactive_controls_have_names_and_are_keyboard_focusable()
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd('\\');
        string exe = Path.Combine(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..")), "src", "Spacewise.Desktop", "bin",
            Path.GetFileName(Path.GetDirectoryName(baseDir)!), Path.GetFileName(baseDir), "Spacewise.Desktop.exe");
        if (!File.Exists(exe)) { Assert.Skip("Spacewise.Desktop is not built for this configuration."); return; }

        using Process app = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })!;
        try
        {
            for (int i = 0; i < 150 && app.MainWindowHandle == IntPtr.Zero && !app.HasExited; i++) { Thread.Sleep(200); app.Refresh(); }
            Assert.NotEqual(IntPtr.Zero, app.MainWindowHandle);
            Thread.Sleep(1500);
            AutomationElement window = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id))!;

            ControlType[] operable = [ControlType.Button, ControlType.CheckBox, ControlType.Edit, ControlType.DataGrid, ControlType.TabItem];
            var problems = new List<string>();
            foreach (ControlType type in operable)
                foreach (AutomationElement element in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type)))
                {
                    // The title bar buttons belong to Windows, not to Spacewise.
                    if (element.Current.AutomationId is "Minimize" or "Maximize" or "Restore" or "Close") continue;
                    string name = element.Current.Name;
                    if (string.IsNullOrWhiteSpace(name)) problems.Add($"{type.ProgrammaticName} without a name");
                    bool focusable = element.Current.IsKeyboardFocusable || type == ControlType.DataGrid;
                    if (element.Current.IsEnabled && !focusable) problems.Add($"{type.ProgrammaticName} '{name}' is not keyboard focusable");
                }
            Assert.Empty(problems);
        }
        finally
        {
            app.CloseMainWindow();
            if (!app.WaitForExit(5000)) app.Kill();
        }
    }
}
