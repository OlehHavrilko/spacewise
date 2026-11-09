using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Spacewise.Data;

// A wrong password fails before SqliteDatabase.Open, so recovery cannot reset a valid database.
public sealed class PortablePasswordKeySource : IDatabaseKeySource, IDisposable
{
    private sealed record Envelope(int Version, byte[] Salt, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
    private readonly string directory;
    private readonly byte[] master;
    private bool disposed;
    private const int Iterations = 600_000;
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("Spacewise portable key v1");

    public PortablePasswordKeySource(string directory, string password)
    {
        if (password.Length < 12) throw new ArgumentException("Пароль должен содержать не менее 12 символов.");
        this.directory = directory;
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "portable-master.json");
        if (File.Exists(file))
        {
            if (new FileInfo(file).Length > 4096) throw new InvalidDataException("Повреждённый ключ portable-версии.");
            Envelope saved = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(file)) ?? throw new InvalidDataException("Пустой ключ.");
            if (saved.Version != 1 || saved.Salt is not { Length: 32 } || saved.Nonce is not { Length: 12 } || saved.Ciphertext is not { Length: 32 } || saved.Tag is not { Length: 16 })
                throw new InvalidDataException("Неподдерживаемый ключ portable-версии.");
            byte[] derived = Rfc2898DeriveBytes.Pbkdf2(password, saved.Salt, Iterations, HashAlgorithmName.SHA256, 32);
            master = new byte[32];
            try
            {
                using var aes = new AesGcm(derived, 16);
                aes.Decrypt(saved.Nonce, saved.Ciphertext, saved.Tag, master, Context);
            }
            catch { CryptographicOperations.ZeroMemory(master); throw; }
            finally { CryptographicOperations.ZeroMemory(derived); }
        }
        else
        {
            master = RandomNumberGenerator.GetBytes(32);
            byte[] salt = RandomNumberGenerator.GetBytes(32), nonce = RandomNumberGenerator.GetBytes(12), cipher = new byte[32], tag = new byte[16];
            byte[] derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
            try
            {
                using var aes = new AesGcm(derived, 16);
                aes.Encrypt(nonce, master, cipher, tag, Context);
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(stream, new Envelope(1, salt, nonce, cipher, tag));
                stream.Flush(flushToDisk: true);
            }
            catch { CryptographicOperations.ZeroMemory(master); throw; }
            finally { CryptographicOperations.ZeroMemory(derived); }
        }
    }

    public byte[] GetKey(string database)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Validate(database);
        string file = Path.Combine(directory, database + ".salt");
        byte[] salt;
        if (File.Exists(file)) salt = File.ReadAllBytes(file);
        else
        {
            salt = RandomNumberGenerator.GetBytes(32);
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(salt); stream.Flush(flushToDisk: true);
        }
        if (salt.Length != 32) throw new InvalidDataException("Повреждённая соль БД.");
        return HMACSHA256.HashData(master, Encoding.UTF8.GetBytes(database).Concat(salt).ToArray());
    }

    public void Reset(string database)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Validate(database);
        string file = Path.Combine(directory, database + ".salt");
        if (File.Exists(file)) File.Move(file, file + "." + Guid.NewGuid().ToString("N") + ".bak");
    }
    private static void Validate(string database)
    {
        if (database.Length is < 1 or > 64 || database.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Недопустимое имя БД.");
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(master); disposed = true; }
}
