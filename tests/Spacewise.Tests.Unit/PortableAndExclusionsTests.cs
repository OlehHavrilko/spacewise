using System.Security.Cryptography;
using Spacewise.Data;

namespace Spacewise.Tests.Unit;

public sealed class PortableAndExclusionsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SpacewisePortable-" + Guid.NewGuid().ToString("N"));
    private const string Password = "test-only portable password";

    [Fact]
    public void Portable_key_survives_move_and_password_reopen()
    {
        byte[] before;
        string original = Path.Combine(root, "original");
        using (var keys = new PortablePasswordKeySource(original, Password))
        {
            before = keys.GetKey("cache");
            Assert.NotEqual(before, keys.GetKey("journal"));
        }
        string moved = Path.Combine(root, "moved"); Directory.Move(original, moved);
        using var reopened = new PortablePasswordKeySource(moved, Password);
        Assert.Equal(before, reopened.GetKey("cache"));
        Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(moved, "portable-master.json")));
    }

    [Fact]
    public void Wrong_password_changes_no_key_files()
    {
        using (var keys = new PortablePasswordKeySource(root, Password)) _ = keys.GetKey("cache");
        byte[] envelope = File.ReadAllBytes(Path.Combine(root, "portable-master.json"));
        byte[] salt = File.ReadAllBytes(Path.Combine(root, "cache.salt"));
        Assert.ThrowsAny<CryptographicException>(() => new PortablePasswordKeySource(root, "incorrect password"));
        Assert.Equal(envelope, File.ReadAllBytes(Path.Combine(root, "portable-master.json")));
        Assert.Equal(salt, File.ReadAllBytes(Path.Combine(root, "cache.salt")));
    }

    [Fact]
    public void Reset_rotates_one_database_and_dispose_prevents_reuse()
    {
        var keys = new PortablePasswordKeySource(root, Password);
        byte[] cache = keys.GetKey("cache"), journal = keys.GetKey("journal");
        keys.Reset("cache");
        Assert.NotEqual(cache, keys.GetKey("cache")); Assert.Equal(journal, keys.GetKey("journal"));
        keys.Dispose(); Assert.Throws<ObjectDisposedException>(() => keys.GetKey("cache"));
    }

    [Fact]
    public void Exclusions_persist_and_reject_relative_or_corrupt_data()
    {
        string file = Path.Combine(root, "exclusions.json");
        var exclusions = new PersistentExclusions(file);
        exclusions.Update([root, root]);
        Assert.Equal(root, Assert.Single(new PersistentExclusions(file).Paths));
        Assert.Throws<InvalidDataException>(() => exclusions.Update(["relative/path"]));
        Assert.Equal(root, Assert.Single(exclusions.Paths));
        File.WriteAllText(file, "null");
        Assert.Throws<InvalidDataException>(() => new PersistentExclusions(file));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
