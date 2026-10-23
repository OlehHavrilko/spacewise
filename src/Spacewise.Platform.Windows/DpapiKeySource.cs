using System.Security.Cryptography;
using System.Text;
using Spacewise.Data;

namespace Spacewise.Platform.Windows;

// ADR 0005: a random 256-bit key per database, stored in <directory>\<db>.key wrapped by DPAPI CurrentUser.
// File format: one version byte, then the ProtectedData blob.
public sealed class DpapiKeySource(string directory) : IDatabaseKeySource
{
    private const byte FormatVersion = 1;

    public byte[] GetKey(string database)
    {
        string file = KeyFile(database);
        if (File.Exists(file))
        {
            byte[] stored = File.ReadAllBytes(file);
            if (stored.Length < 2 || stored[0] != FormatVersion)
                throw new CryptographicException($"Неизвестный формат ключа {Path.GetFileName(file)}.");
            return ProtectedData.Unprotect(stored.AsSpan(1).ToArray(), Entropy(database), DataProtectionScope.CurrentUser);
        }

        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped = ProtectedData.Protect(key, Entropy(database), DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(directory);
        // Write to a temp name, then move: a crash never leaves a truncated key file.
        string temp = file + ".tmp";
        File.WriteAllBytes(temp, [FormatVersion, .. wrapped]);
        File.Move(temp, file);
        return key;
    }

    public void Reset(string database)
    {
        string file = KeyFile(database);
        if (File.Exists(file)) File.Move(file, $"{file}.unreadable-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}");
    }

    private string KeyFile(string database) => Path.Combine(directory, database + ".key");

    private static byte[] Entropy(string database) => Encoding.UTF8.GetBytes($"Spacewise/{database}/v1");
}
