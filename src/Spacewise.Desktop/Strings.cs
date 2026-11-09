using System.Globalization;
using System.Windows;

namespace Spacewise.Desktop;

// UI text uses the same merged dictionary as XAML. Missing keys fail loudly.
internal static class Strings
{
    public static string Get(string key)
    {
        if (Application.Current is { } app) return (string)app.FindResource(key);
        var resources = new ResourceDictionary { Source = new Uri("/Spacewise.Desktop;component/Resources/Strings.ru-RU.xaml", UriKind.Relative) };
        return (string)resources[key];
    }

    // Keep interpolation's current-culture semantics, including date and numeric formats.
    // Arguments are values, never concatenated into the composite format string.
    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}
