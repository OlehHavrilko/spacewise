using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace Spacewise.Desktop;

public enum ThemeChoice { System = 0, Light = 1, Dark = 2 }

internal sealed record AppSettings(ThemeChoice Theme = ThemeChoice.System)
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spacewise", "settings.json");

    // A missing or damaged file means defaults; settings never block startup.
    public static AppSettings Load()
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            return loaded is not null && Enum.IsDefined(loaded.Theme) ? loaded : new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* losing a preference is harmless */ }
    }
}

// Light/Dark palettes for our own styles plus the built-in WPF Fluent theme for stock controls.
// High contrast always wins: system colors, no Fluent.
internal static class ThemeService
{
    public static ThemeChoice Current { get; private set; }

    public static void Initialize()
    {
        Apply(AppSettings.Load().Theme);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color && Current == ThemeChoice.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
        };
    }

    public static void Apply(ThemeChoice choice)
    {
        Current = choice;
        Application app = Application.Current;
        bool highContrast = SystemParameters.HighContrast;
        bool dark = !highContrast && (choice == ThemeChoice.Dark || (choice == ThemeChoice.System && SystemUsesDarkTheme()));
        string palette = highContrast ? "System" : dark ? "Dark" : "Light";
        app.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"/Spacewise.Desktop;component/Resources/Theme.{palette}.xaml", UriKind.Relative),
        };
        app.ThemeMode = highContrast ? ThemeMode.None : dark ? ThemeMode.Dark : ThemeMode.Light;
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch { return false; }
    }
}
