// Spacewise storage spike (Q14/Q16). Not production code.
// Usage: dotnet run -c Release [-- --dpapi]
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Spacewise.Spikes.Storage;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class Probe
{
    private static readonly Version Required = new(3, 51, 3);

    private static int Main(string[] args)
    {
        string variant = typeof(Probe).Assembly.GetName().Name ?? "?";
        Console.WriteLine($"=== variant: {variant}");
#if EXPLICIT_INIT
        SQLitePCL.Batteries_V2.Init();
#endif
        string dir = Path.Combine(Path.GetTempPath(), "spacewise-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        int rc = 0;
        try
        {
            rc = RunSqlite(dir);
            if (args.Contains("--dpapi"))
            {
                RunCrypto();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
            Console.WriteLine($"cleanup: {(Directory.Exists(dir) ? "FAILED" : "ok")}");
        }

        return rc;
    }

    private static int RunSqlite(string dir)
    {
        using (var mem = new SqliteConnection("Data Source=:memory:"))
        {
            mem.Open();
            string ver = Scalar(mem, "select sqlite_version()");
            var opts = Rows(mem, "pragma compile_options");
            Console.WriteLine($"provider: {SQLitePCL.raw.GetNativeLibraryName()}");
            Console.WriteLine($"sqlite_version: {ver}  (>= {Required}: {(Version.Parse(ver) >= Required ? "YES" : "NO")})");
            string[] interesting = ["THREADSAFE", "FTS5", "JSON", "HAS_CODEC", "SQLITE3MC", "SQLCIPHER", "CIPHER", "TEMP_STORE", "DQS", "DEFAULT_FOREIGN_KEYS", "RTREE", "MATH"];
            Console.WriteLine($"compile_options ({opts.Count}): " + string.Join(", ", opts.Where(o => interesting.Any(o.Contains))));
            string cipher = TryScalar(mem, "pragma cipher_version");
            Console.WriteLine($"pragma cipher_version: {(string.IsNullOrEmpty(cipher) ? "<none>" : cipher)}");
        }

        string plainFile = Path.Combine(dir, "plain.db");
        double plainMs = FileTest(plainFile, password: null);

        // Encryption check: only meaningful if the native lib has a codec.
        string encFile = Path.Combine(dir, "enc.db");
        try
        {
            double encMs = FileTest(encFile, password: "probe-key-not-secret");
            byte[] head = File.ReadAllBytes(encFile).AsSpan(0, 16).ToArray();
            bool plainHeader = Encoding.ASCII.GetString(head).StartsWith("SQLite format 3", StringComparison.Ordinal);
            Console.WriteLine($"encrypted file header plaintext: {plainHeader} (false => file is encrypted)");
            bool wrongKeyFails;
            try
            {
                using var bad = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = encFile, Password = "wrong", Pooling = false }.ToString());
                bad.Open();
                Scalar(bad, "select count(*) from paths");
                wrongKeyFails = false;
            }
            catch (SqliteException)
            {
                wrongKeyFails = true;
            }

            Console.WriteLine($"wrong key rejected: {wrongKeyFails}");
            Console.WriteLine($"insert 100k rows: plain {plainMs:F0} ms, encrypted {encMs:F0} ms ({encMs / plainMs:P0})");
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            Console.WriteLine($"encryption: not supported ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})");
            Console.WriteLine($"insert 100k rows: plain {plainMs:F0} ms");
        }

        return 0;
    }

    private static double FileTest(string file, string? password)
    {
        var csb = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false };
        if (password is not null)
        {
            csb.Password = password;
        }

        using var c = new SqliteConnection(csb.ToString());
        c.Open();
        string jm = Scalar(c, "pragma journal_mode=WAL");
        Exec(c, "pragma synchronous=FULL");
        string sync = Scalar(c, "pragma synchronous");
        Console.WriteLine($"[{Path.GetFileName(file)}] journal_mode=WAL -> {jm}; synchronous=FULL -> {sync} (2=FULL)");
        Exec(c, "create table paths(id integer primary key, parent_id integer, name text)");
        var sw = Stopwatch.StartNew();
        using (var tx = c.BeginTransaction())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "insert into paths(parent_id, name) values ($p, $n)";
            var p = cmd.Parameters.Add("$p", SqliteType.Integer);
            var n = cmd.Parameters.Add("$n", SqliteType.Text);
            for (int i = 0; i < 100_000; i++)
            {
                p.Value = i / 50;
                n.Value = "file_" + i.ToString("D6") + ".tmp";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        sw.Stop();
        Console.WriteLine($"[{Path.GetFileName(file)}] plaintext 'file_0' in -wal: {ContainsPlain(file + "-wal")}");
        Exec(c, "pragma wal_checkpoint(TRUNCATE)");
        Console.WriteLine($"[{Path.GetFileName(file)}] plaintext 'file_0' in db after checkpoint: {ContainsPlain(file)}");
        return sw.Elapsed.TotalMilliseconds;
    }

    private static void RunCrypto()
    {
        const int N = 100_000;
        var rnd = new Random(42);
        string[] seg = ["Users", "olehh", "AppData", "Local", "Packages", "Microsoft.WindowsStore_8wekyb3d8bbwe", "LocalCache", "Temp", "node_modules", "@types", "Program Files", "Steam", "steamapps", "common", "shadercache", "Пользователи", "Документы"];
        var full = new string[N];
        var names = new string[N];
        for (int i = 0; i < N; i++)
        {
            int depth = rnd.Next(3, 9);
            var sb = new StringBuilder("C:");
            for (int d = 0; d < depth; d++)
            {
                sb.Append('\\').Append(seg[rnd.Next(seg.Length)]);
            }

            names[i] = "file_" + rnd.Next(1_000_000) + (rnd.Next(2) == 0 ? ".tmp" : ".cache");
            sb.Append('\\').Append(names[i]);
            full[i] = sb.ToString();
        }

        Console.WriteLine($"--- crypto, N={N}, CurrentUser scope");
        Dpapi("full path", full);
        Dpapi("name only", names);

        // Envelope alternative: one DPAPI-wrapped 256-bit key, AES-GCM per field.
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
        byte[] unwrapped = ProtectedData.Unprotect(wrapped, null, DataProtectionScope.CurrentUser);
        Console.WriteLine($"wrapped key blob: {wrapped.Length} B, roundtrip ok: {unwrapped.AsSpan().SequenceEqual(key)}");
        AesGcmBench("full path", full, key);
        AesGcmBench("name only", names, key);

        var sw = Stopwatch.StartNew();
        using var hmac = new HMACSHA256(key);
        foreach (string s in names)
        {
            hmac.ComputeHash(Encoding.UTF8.GetBytes(s.ToUpperInvariant()));
        }

        Console.WriteLine($"HMAC-SHA256 blind index (name only): {sw.Elapsed.TotalMilliseconds:F0} ms, 32 B/field (or 16 B truncated)");
    }

    private static void Dpapi(string label, string[] data)
    {
        var plain = data.Select(Encoding.UTF8.GetBytes).ToArray();
        var enc = new byte[plain.Length][];
        Protect(plain[0]); // warm-up
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < plain.Length; i++)
        {
            enc[i] = Protect(plain[i]);
        }

        double protectMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        for (int i = 0; i < enc.Length; i++)
        {
            ProtectedData.Unprotect(enc[i], null, DataProtectionScope.CurrentUser);
        }

        double unprotectMs = sw.Elapsed.TotalMilliseconds;
        double avgPlain = plain.Average(p => p.Length);
        double avgEnc = enc.Average(p => p.Length);
        Console.WriteLine($"DPAPI {label}: protect {protectMs:F0} ms ({protectMs * 1000 / data.Length:F1} us/op), unprotect {unprotectMs:F0} ms ({unprotectMs * 1000 / data.Length:F1} us/op); avg {avgPlain:F0} B -> {avgEnc:F0} B (+{avgEnc - avgPlain:F0} B)");

        static byte[] Protect(byte[] p) => ProtectedData.Protect(p, null, DataProtectionScope.CurrentUser);
    }

    private static void AesGcmBench(string label, string[] data, byte[] key)
    {
        using var aes = new AesGcm(key, 16);
        var plain = data.Select(Encoding.UTF8.GetBytes).ToArray();
        var enc = new byte[plain.Length][];
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < plain.Length; i++)
        {
            byte[] blob = new byte[12 + 16 + plain[i].Length];
            RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
            aes.Encrypt(blob.AsSpan(0, 12), plain[i], blob.AsSpan(28), blob.AsSpan(12, 16));
            enc[i] = blob;
        }

        double encMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        foreach (byte[] blob in enc)
        {
            byte[] outp = new byte[blob.Length - 28];
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), outp);
        }

        double decMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"AES-GCM {label}: encrypt {encMs:F0} ms, decrypt {decMs:F0} ms; overhead +28 B/field");
    }

    private static bool ContainsPlain(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray().AsSpan().IndexOf("file_0"u8) >= 0;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string TryScalar(SqliteConnection c, string sql)
    {
        try
        {
            return Scalar(c, sql);
        }
        catch (SqliteException)
        {
            return string.Empty;
        }
    }

    private static List<string> Rows(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read())
        {
            list.Add(r.GetString(0));
        }

        return list;
    }
}
