using Spacewise.Core;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

public sealed class QuarantineFileRemoverTests : IDisposable
{
    private readonly FsFixture fx = new();
    private readonly string quarantine;
    private readonly QuarantineFileRemover remover;

    public QuarantineFileRemoverTests()
    {
        quarantine = fx.Dir("quarantine");
        remover = new QuarantineFileRemover(quarantine);
    }

    public void Dispose()
    {
        // Quarantined files were created by the remover, not the fixture: remove those names first.
        foreach (string file in Directory.GetFiles(quarantine)) File.Delete(file);
        fx.Dispose();
    }

    private static PlanItem ItemFor(string path) =>
        new("test", 1, "delete", path, new FileInfo(path).Length, new FileInfo(path).LastWriteTimeUtc);

    [Fact]
    public void Moves_file_into_quarantine_keeping_its_content()
    {
        string file = fx.File("a.bin", 10);
        File.WriteAllText(file, "payload");
        PlanItem item = ItemFor(file);

        RemoveResult result = remover.Remove(item);

        Assert.True(result.Status == RemoveStatus.Removed, $"{result.Status} code {result.Code}");
        Assert.False(File.Exists(file));
        Assert.Equal("payload", File.ReadAllText(result.StoredPath!).TrimEnd('\0'));
        Assert.Equal(Path.TrimEndingDirectorySeparator(quarantine), Path.GetDirectoryName(result.StoredPath));
    }

    [Fact]
    public void Restore_puts_the_file_back_and_refuses_to_overwrite()
    {
        string file = fx.File("b.bin", 10);
        RemoveResult result = remover.Remove(ItemFor(file));
        Assert.True(result.Status == RemoveStatus.Removed, $"{result.Status} code {result.Code}");

        File.WriteAllText(file, "newer");
        RestoreResult blocked = remover.Restore(result.StoredPath!, file);
        Assert.False(blocked.Restored);
        Assert.Equal("newer", File.ReadAllText(file));

        File.Delete(file);
        Assert.True(remover.Restore(result.StoredPath!, file).Restored);
        Assert.Equal(10, new FileInfo(file).Length);
        Assert.False(File.Exists(result.StoredPath));
    }

    [Fact]
    public void Changed_file_is_not_quarantined()
    {
        string file = fx.File("c.bin", 10);
        Assert.Equal(RemoveStatus.Changed, remover.Remove(ItemFor(file) with { ExpectedSize = 3 }).Status);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Restore_rejects_a_stored_path_outside_the_quarantine()
    {
        string file = fx.File("d.bin", 10);
        Assert.False(remover.Restore(file, Path.Combine(fx.Root, "e.bin")).Restored);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Move_records_the_sha256_of_the_content()
    {
        string file = fx.File("s.bin", 0);
        File.WriteAllText(file, "hash me");
        RemoveResult result = remover.Remove(ItemFor(file));
        Assert.Equal(RemoveStatus.Removed, result.Status);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData("hash me"u8), result.Sha256);
    }

    [Fact]
    public void Purge_deletes_a_quarantined_file_for_good()
    {
        string file = fx.File("p.bin", 10);
        RemoveResult moved = remover.Remove(ItemFor(file));
        Assert.Equal(RemoveStatus.Removed, remover.Purge(moved.StoredPath!).Status);
        Assert.False(File.Exists(moved.StoredPath));
        Assert.Equal(RemoveStatus.Gone, remover.Purge(moved.StoredPath!).Status);
        Assert.False(remover.Restore(moved.StoredPath!, file).Restored);
    }

    [Fact]
    public void Purge_refuses_files_outside_the_quarantine_directory()
    {
        string file = fx.File("outside.bin", 10);
        Assert.Equal(RemoveStatus.Unsafe, remover.Purge(file).Status);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Purge_removes_a_read_only_quarantined_file()
    {
        string file = fx.File("ro.bin", 10);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        RemoveResult moved = remover.Remove(ItemFor(file));
        Assert.Equal(RemoveStatus.Removed, remover.Purge(moved.StoredPath!).Status);
        Assert.False(File.Exists(moved.StoredPath));
    }
}
