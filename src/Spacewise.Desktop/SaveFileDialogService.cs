using Microsoft.Win32;

namespace Spacewise.Desktop;

// Asks where to save an export; null when the user cancels.
public interface IFileDialogs
{
    string? SaveFile(string title, string defaultName, string filter);
}

public sealed class SaveFileDialogService : IFileDialogs
{
    public string? SaveFile(string title, string defaultName, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = defaultName, Filter = filter, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
