using System.Security.Cryptography;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

public sealed class DpapiKeySourceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "SpacewiseKeys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(dir)) return;
        foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
        Directory.Delete(dir);
    }

    [Fact]
    public void Key_is_created_once_and_stored_wrapped()
    {
        var keys = new DpapiKeySource(dir);
        byte[] first = keys.GetKey("cache");
        byte[] again = new DpapiKeySource(dir).GetKey("cache");

        Assert.Equal(32, first.Length);
        Assert.Equal(first, again);
        Assert.NotEqual(first, keys.GetKey("journal"));
        byte[] stored = File.ReadAllBytes(Path.Combine(dir, "cache.key"));
        Assert.Equal(1, stored[0]);
        Assert.False(stored.AsSpan().IndexOf(first) >= 0, "raw key must not be on disk");
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Entropy_binds_key_file_to_its_database()
    {
        var keys = new DpapiKeySource(dir);
        keys.GetKey("cache");
        File.Copy(Path.Combine(dir, "cache.key"), Path.Combine(dir, "journal.key"));
        Assert.ThrowsAny<CryptographicException>(() => keys.GetKey("journal"));
    }

    [Fact]
    public void Reset_sets_old_key_aside_and_issues_a_new_one()
    {
        var keys = new DpapiKeySource(dir);
        byte[] old = keys.GetKey("cache");
        keys.Reset("cache");

        Assert.Single(Directory.GetFiles(dir, "cache.key.unreadable-*"));
        Assert.NotEqual(old, keys.GetKey("cache"));
    }
}
