using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Spacewise.Core;

namespace Spacewise.Platform.Linux;

public sealed record LinuxHeldFile(string Id, string OriginalPath, long Size, DateTime MtimeUtc,
    ulong Device, ulong Inode, string Sha256, DateTime CreatedUtc);

// Write-ahead manifest + same-filesystem rename. No cross-volume copy/delete fallback.
// A crash before/after rename is recoverable by listing manifests that still have a payload.
public sealed class LinuxQuarantine(string dataDirectory, ICleanupPathPolicy policy) : IFileRemover
{
    private readonly string root = Path.Combine(dataDirectory, "quarantine");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private SafeFileHandle OpenRoot()
    {
        using var data = LinuxNative.OpenDirectory(dataDirectory, create: true);
        LinuxNative.RequirePrivate(data);
        if (LinuxNative.mkdirat(LinuxNative.Fd(data), "quarantine", 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17)
            throw new IOException("Не удалось создать карантин.");
        var handle = LinuxNative.Handle(LinuxNative.openat(LinuxNative.Fd(data), "quarantine",
            LinuxNative.DirectoryFlag | LinuxNative.NoFollow | LinuxNative.CloseOnExec));
        try { LinuxNative.RequirePrivate(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    public RemoveResult Remove(PlanItem item)
    {
        if (item.ActionId != "delete" || item.Identity is null || !policy.Check(item.Path).Allowed) return new(RemoveStatus.Unsafe);
        try
        {
            using var parent = LinuxNative.OpenDirectory(Path.GetDirectoryName(item.Path)!);
            string name = Path.GetFileName(item.Path);
            using var source = LinuxNative.Handle(LinuxNative.openat(LinuxNative.Fd(parent), name,
                LinuxNative.NoFollow | LinuxNative.CloseOnExec | 0x800)); // O_NONBLOCK: never block on a swapped FIFO.
            LinuxNative.Stat before = LinuxNative.Read(LinuxNative.Fd(source));
            if (before.Type != LinuxNative.Regular || before.Links != 1 || before.Uid != LinuxNative.geteuid()) return new(RemoveStatus.Unsafe);
            if (!Matches(before, item)) return new(RemoveStatus.Changed);
            using var content = new FileStream(source, FileAccess.Read);
            string hash = Convert.ToHexString(SHA256.HashData(content));
            if (!Matches(LinuxNative.Read(LinuxNative.Fd(source)), item)) return new(RemoveStatus.Changed);
            using var quarantine = OpenRoot();
            string id = Guid.NewGuid().ToString("N");
            if (LinuxNative.mkdirat(LinuxNative.Fd(quarantine), id, 0x1c0) != 0) return new(RemoveStatus.Failed, Marshal.GetLastPInvokeError());
            using var transaction = LinuxNative.Handle(LinuxNative.openat(LinuxNative.Fd(quarantine), id,
                LinuxNative.DirectoryFlag | LinuxNative.NoFollow | LinuxNative.CloseOnExec));
            var held = new LinuxHeldFile(id, item.Path, item.ExpectedSize, item.ExpectedMtimeUtc,
                before.Identity.VolumeSerial, before.Inode, hash, DateTime.UtcNow);
            using (var manifestHandle = LinuxNative.Handle(LinuxNative.openat(LinuxNative.Fd(transaction), "manifest.json",
                1 | 0x40 | 0x80 | LinuxNative.NoFollow | LinuxNative.CloseOnExec, 0x180)))
            using (var manifest = new FileStream(manifestHandle, FileAccess.Write))
            {
                JsonSerializer.Serialize(manifest, held, Json);
                manifest.Flush(flushToDisk: true);
            }
            LinuxNative.Sync(transaction);
            LinuxNative.Sync(quarantine);
            if (LinuxNative.renameat2(LinuxNative.Fd(parent), name, LinuxNative.Fd(transaction), "payload", 1) != 0)
                return new(RemoveStatus.Failed, Marshal.GetLastPInvokeError());
            LinuxNative.Sync(parent);
            LinuxNative.Sync(transaction);
            // A name could have changed between checking and rename. Keep it recoverable,
            // report Changed, and never purge it using the old manifest.
            LinuxNative.Stat moved = LinuxNative.Read(LinuxNative.Fd(transaction), "payload");
            return new(Matches(moved, item) ? RemoveStatus.Removed : RemoveStatus.Changed,
                StoredPath: Path.Combine(root, id, "payload"), Sha256: Convert.FromHexString(hash));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(RemoveStatus.Failed, e.HResult); }
    }

    public IReadOnlyList<LinuxHeldFile> List()
    {
        using var quarantine = OpenRoot();
        var result = new List<LinuxHeldFile>();
        foreach (string entry in Directory.EnumerateDirectories("/proc/self/fd/" + LinuxNative.Fd(quarantine)))
        {
            string id = Path.GetFileName(entry);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            using var transaction = OpenTransaction(quarantine, id);
            if (!File.Exists("/proc/self/fd/" + LinuxNative.Fd(transaction) + "/payload")) continue;
            result.Add(ReadManifest(transaction, id));
        }
        return result;
    }

    public RestoreResult Restore(string id)
    {
        try
        {
            using var quarantine = OpenRoot();
            using var transaction = OpenTransaction(quarantine, id);
            LinuxHeldFile held = ReadManifest(transaction, id);
            using var payload = VerifiedPayload(transaction, held);
            if (payload is null) return new(false, HashMismatch: true);
            string target = LinuxPaths.Normalize(held.OriginalPath);
            using var parent = LinuxNative.OpenDirectory(Path.GetDirectoryName(target)!);
            if (LinuxNative.renameat2(LinuxNative.Fd(transaction), "payload", LinuxNative.Fd(parent), Path.GetFileName(target), 1) != 0)
                return new(false, Marshal.GetLastPInvokeError());
            LinuxNative.Sync(parent);
            LinuxNative.Sync(transaction);
            return new(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(false, e.HResult); }
    }

    // Explicit permanent purge only. No automatic retention policy in the initial Linux release.
    public bool Purge(string id)
    {
        using var quarantine = OpenRoot();
        using var transaction = OpenTransaction(quarantine, id);
        LinuxHeldFile held = ReadManifest(transaction, id);
        using var payload = VerifiedPayload(transaction, held);
        if (payload is null) return false;
        if (LinuxNative.unlinkat(LinuxNative.Fd(transaction), "payload", 0) != 0) return false;
        LinuxNative.Sync(transaction);
        return true;
    }

    private static bool Matches(LinuxNative.Stat stat, PlanItem item) => stat.Type == LinuxNative.Regular
        && stat.Identity == item.Identity && stat.Size == (ulong)item.ExpectedSize && stat.Mtime == item.ExpectedMtimeUtc;

    private static SafeFileHandle OpenTransaction(SafeFileHandle quarantine, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Недопустимая запись карантина.");
        var handle = LinuxNative.Handle(LinuxNative.openat(LinuxNative.Fd(quarantine), id,
            LinuxNative.DirectoryFlag | LinuxNative.NoFollow | LinuxNative.CloseOnExec));
        try { LinuxNative.RequirePrivate(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static LinuxHeldFile ReadManifest(SafeFileHandle transaction, string id)
    {
        using var fd = LinuxNative.Handle(LinuxNative.openat(LinuxNative.Fd(transaction), "manifest.json", LinuxNative.NoFollow | LinuxNative.CloseOnExec | 0x800));
        LinuxNative.Stat stat = LinuxNative.Read(LinuxNative.Fd(fd));
        if (stat.Type != LinuxNative.Regular || stat.Size > 65536) throw new IOException("Повреждённая запись карантина.");
        using var stream = new FileStream(fd, FileAccess.Read);
        LinuxHeldFile held = JsonSerializer.Deserialize<LinuxHeldFile>(stream, Json) ?? throw new IOException("Пустая запись карантина.");
        if (held.Id != id || held.Size < 0 || held.Sha256.Length != 64) throw new IOException("Повреждённая запись карантина.");
        return held;
    }

    private static FileStream? VerifiedPayload(SafeFileHandle transaction, LinuxHeldFile held)
    {
        var fd = LinuxNative.Handle(LinuxNative.openat(LinuxNative.Fd(transaction), "payload", LinuxNative.NoFollow | LinuxNative.CloseOnExec | 0x800));
        FileStream? stream = null;
        try
        {
            LinuxNative.Stat stat = LinuxNative.Read(LinuxNative.Fd(fd));
            if (stat.Type != LinuxNative.Regular || stat.Links != 1 || stat.Identity != new FileIdentity(held.Device, held.Inode)
                || stat.Size != (ulong)held.Size) { fd.Dispose(); return null; }
            stream = new FileStream(fd, FileAccess.Read);
            if (Convert.ToHexString(SHA256.HashData(stream)) == held.Sha256) return stream;
            stream.Dispose();
            return null;
        }
        catch { if (stream is null) fd.Dispose(); else stream.Dispose(); throw; }
    }
}
