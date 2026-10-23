using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Spacewise.Data;

// Migration N (1-based) brings PRAGMA user_version from N-1 to N.
public sealed record DatabaseSchema(string Name, bool DurableWrites, IReadOnlyList<string> Migrations)
{
    public int Version => Migrations.Count;
}

// Supplies the 256-bit key of one database (ADR 0005). The caller zeroes the returned array.
public interface IDatabaseKeySource
{
    byte[] GetKey(string database);
    // The key can no longer open the database: set it aside and start a new one.
    void Reset(string database);
}

// RecoveredFrom is set when an unreadable database was renamed and a new one created.
public sealed record OpenedDatabase(SqliteConnection Connection, string? RecoveredFrom);

public static class SqliteDatabase
{
    private const int NotADatabase = 26;

    // Opens or creates an encrypted database and migrates it to the schema version.
    // An unreadable file (wrong or lost key, corruption) is renamed, never deleted (ADR 0005).
    public static OpenedDatabase Open(string path, DatabaseSchema schema, IDatabaseKeySource keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try { return new(Connect(path, schema, keys), null); }
        catch (Exception e) when (e is SqliteException { SqliteErrorCode: NotADatabase } or CryptographicException)
        {
            string moved = SetAside(path);
            keys.Reset(schema.Name);
            return new(Connect(path, schema, keys), moved);
        }
    }

    private static SqliteConnection Connect(string path, DatabaseSchema schema, IDatabaseKeySource keys)
    {
        SqliteConnection connection = OpenKeyed(path, schema.Name, keys);
        try
        {
            Execute(connection, "PRAGMA journal_mode = WAL");
            Execute(connection, "PRAGMA foreign_keys = ON");
            Execute(connection, schema.DurableWrites ? "PRAGMA synchronous = FULL" : "PRAGMA synchronous = NORMAL");
            Migrate(connection, path, schema, keys);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection OpenKeyed(string path, string name, IDatabaseKeySource keys)
    {
        // No pooling: a pooled connection would outlive Dispose and keep the file open for SetAside.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            byte[] key = keys.GetKey(name);
            try
            {
                // Raw key via PRAGMA, not the connection string, and an explicit cipher (ADR 0005).
                // The hex copy is a managed string and cannot be zeroed.
                Execute(connection, "PRAGMA cipher = 'chacha20'");
                Execute(connection, $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\"");
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            // A wrong key surfaces here as SQLITE_NOTADB.
            Scalar(connection, "SELECT count(*) FROM sqlite_master");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Migrate(SqliteConnection connection, string path, DatabaseSchema schema, IDatabaseKeySource keys)
    {
        int version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > schema.Version)
            throw new InvalidOperationException($"{schema.Name}: версия схемы {version} новее поддерживаемой {schema.Version}.");
        if (version == schema.Version) return;
        if (version > 0)
        {
            // FR-DATA-001: back up before migrating an existing database; the copy keeps the same key.
            using SqliteConnection backup = OpenKeyed($"{path}.v{version}.bak", schema.Name, keys);
            connection.BackupDatabase(backup);
        }
        for (int next = version; next < schema.Version; next++)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, schema.Migrations[next], transaction);
            Execute(connection, $"PRAGMA user_version = {next + 1}", transaction);
            transaction.Commit();
        }
    }

    private static string SetAside(string path)
    {
        string target = $"{path}.unreadable-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}";
        foreach (string suffix in new[] { "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Move(path + suffix, target + suffix);
        File.Move(path, target);
        return target;
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
