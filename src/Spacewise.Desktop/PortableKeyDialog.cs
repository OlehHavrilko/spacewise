using System.Windows;
using System.Windows.Controls;

namespace Spacewise.Desktop;

internal static class PortableKeyDialog
{
    public static string Password(bool create)
    {
        var password = new PasswordBox { Margin = new Thickness(0, 8, 0, 8) };
        var repeat = new PasswordBox { Margin = new Thickness(0, 8, 0, 8), Visibility = create ? Visibility.Visible : Visibility.Collapsed };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var accept = new Button { Content = Strings.Get(create ? "Portable.Create" : "Portable.Open"), Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        var cancel = new Button { Content = Strings.Get("Portable.Cancel"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var window = new Window { Title = "Spacewise — portable", Width = 500, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Strings.Get(create ? "Portable.CreatePrompt" : "Portable.OpenPrompt"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(password);
        if (create) panel.Children.Add(new TextBlock { Text = Strings.Get("Portable.Repeat") });
        panel.Children.Add(repeat); panel.Children.Add(error);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { cancel, accept } });
        accept.Click += (_, _) =>
        {
            if (password.Password.Length < 12) { error.Text = Strings.Get("Portable.TooShort"); return; }
            if (create && password.Password != repeat.Password) { error.Text = Strings.Get("Portable.Mismatch"); return; }
            window.DialogResult = true;
        };
        window.Content = panel;
        window.Loaded += (_, _) => password.Focus();
        ShutdownMode? previous = Application.Current?.ShutdownMode;
        if (Application.Current is { } app) app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            if (window.ShowDialog() != true) throw new OperationCanceledException();
        }
        finally { if (Application.Current is { } current && previous is { } mode) current.ShutdownMode = mode; }
        return password.Password;
    }
}
