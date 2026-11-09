using CommunityToolkit.Mvvm.ComponentModel;
using Spacewise.Core;

namespace Spacewise.Desktop;

public sealed partial class FolderNode(string path, long logicalBytes, long files) : ObservableObject
{
    public string Path { get; } = path;
    public long LogicalBytes { get; } = logicalBytes;
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
    public string Label => $"{Name} · {DisplayFormat.Size(LogicalBytes)}";
    public string Description => $"{Path}\n{DisplayFormat.Size(LogicalBytes)} · {DisplayFormat.Count(files)}";
    public List<FolderNode> Children { get; } = [];
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public static FolderNode Build(ScanResult result)
    {
        var root = new FolderNode(result.Root, result.LogicalBytes, result.Files) { IsExpanded = true };
        var nodes = result.Folders.ToDictionary(f => f.Path, f => new FolderNode(f.Path, f.LogicalBytes, f.Files), StringComparer.OrdinalIgnoreCase);
        foreach (FolderNode node in nodes.Values)
        {
            string? parent = System.IO.Path.GetDirectoryName(node.Path);
            (parent is not null && nodes.TryGetValue(parent, out FolderNode? found) ? found : root).Children.Add(node);
        }
        return root;
    }
}
