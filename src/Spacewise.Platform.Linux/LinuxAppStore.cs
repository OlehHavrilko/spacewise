using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Spacewise.Core;

namespace Spacewise.Platform.Linux;

public sealed record LinuxSettings(IReadOnlyList<string> Exclusions);
public sealed record LinuxScanSummary(DateTime Utc, string Root, long Files, long Bytes, ScanState State);
public sealed record LinuxFolderSnapshot(DateTime Utc, string Root, IReadOnlyList<FolderAggregate> Folders);

// Data is protected by Unix permissions, not encrypted at rest. No DPAPI dependency.
public sealed class LinuxAppStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public LinuxSettings Settings() => Read<LinuxSettings>("settings.json") ?? new([]);
    public IReadOnlyList<LinuxScanSummary> History() => Read<LinuxScanSummary[]>("history.json") ?? [];
    public IReadOnlyList<LinuxFolderSnapshot> FolderHistory() => Read<LinuxFolderSnapshot[]>("folder-history.json") ?? [];
    public void SaveSettings(LinuxSettings settings) => Write("settings.json", settings);
    public void SaveScan(ScanResult scan)
    {
        DateTime now = DateTime.UtcNow;
        Write("history.json", History().Append(new LinuxScanSummary(now, scan.Root, scan.Files, scan.LogicalBytes, scan.State)).TakeLast(100).ToArray());
        if (scan.State != ScanState.Completed || scan.FolderAggregationTruncated) return;
        var snapshots = FolderHistory().Where(s => s.Utc >= now.Date.AddDays(-30) && !(s.Root == scan.Root && s.Utc.Date == now.Date))
            .Append(new LinuxFolderSnapshot(now, scan.Root, new[] { new FolderAggregate(scan.Root, scan.Files, scan.LogicalBytes) }.Concat(scan.Folders.Take(64)).ToArray())).ToArray();
        string[] roots = snapshots.OrderByDescending(s => s.Utc).Select(s => s.Root).Distinct(StringComparer.Ordinal).Take(8).ToArray();
        Write("folder-history.json", snapshots.Where(s => roots.Contains(s.Root, StringComparer.Ordinal)).ToArray());
    }

    private SafeFileHandle Open()
    {
        var fd = LinuxNative.OpenDirectory(directory, create: true);
        try { LinuxNative.RequirePrivate(fd); return fd; }
        catch { fd.Dispose(); throw; }
    }

    private T? Read<T>(string file)
    {
        using var dir = Open();
        int raw = LinuxNative.openat(LinuxNative.Fd(dir), file, LinuxNative.NoFollow | LinuxNative.CloseOnExec | 0x800);
        if (raw < 0 && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 2) return default;
        using var fd = LinuxNative.Handle(raw);
        var stat = LinuxNative.Read(LinuxNative.Fd(fd));
        if (stat.Type != LinuxNative.Regular || stat.Size > 32 * 1024 * 1024) throw new IOException("Недопустимый файл настроек или истории.");
        using var stream = new FileStream(fd, FileAccess.Read);
        return JsonSerializer.Deserialize<T>(stream, Json);
    }

    private void Write<T>(string file, T value)
    {
        using var dir = Open();
        string temporary = "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fd = LinuxNative.Handle(LinuxNative.openat(LinuxNative.Fd(dir), temporary,
            1 | 0x40 | 0x80 | LinuxNative.NoFollow | LinuxNative.CloseOnExec, 0x180)))
        using (var stream = new FileStream(fd, FileAccess.Write))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(flushToDisk: true);
        }
        if (LinuxNative.renameat2(LinuxNative.Fd(dir), temporary, LinuxNative.Fd(dir), file, 0) != 0)
            throw new IOException("Не удалось сохранить данные.");
        LinuxNative.Sync(dir);
    }
}
