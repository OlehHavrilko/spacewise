using System.Text.Json;
using Spacewise.Core;

namespace Spacewise.Data;

// Fail closed on damaged data: silently forgetting a user's protection would widen cleanup.
public sealed class PersistentExclusions
{
    private readonly string file;
    private string[] paths;
    public IReadOnlyList<string> Paths => Array.AsReadOnly(Volatile.Read(ref paths));
    public PersistentExclusions(string file)
    {
        this.file = file;
        paths = File.Exists(file) ? Normalize(JsonSerializer.Deserialize<string[]>(File.ReadAllText(file))
            ?? throw new InvalidDataException("Пустой список исключений.")) : [];
    }
    public void Update(IEnumerable<string> value)
    {
        string[] normalized = Normalize(value);
        if (normalized.SequenceEqual(Volatile.Read(ref paths), StringComparer.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, normalized);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, file, overwrite: true);
        Volatile.Write(ref paths, normalized);
    }
    private static string[] Normalize(IEnumerable<string> value) => value.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p =>
    {
        if (!Path.IsPathFullyQualified(ScanPaths.Unquote(p))) throw new InvalidDataException("Исключение должно быть абсолютным путём.");
        return ScanPaths.Normalize(p);
    }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
