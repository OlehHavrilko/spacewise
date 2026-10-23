using System.Windows;

namespace Spacewise.Desktop;

// "No" is the default button: Enter or Esc never starts a cleanup.
public sealed class MessageBoxConfirmation : IConfirmation
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
