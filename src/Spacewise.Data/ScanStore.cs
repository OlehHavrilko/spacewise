using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Spacewise.Core;

namespace Spacewise.Data;

public sealed record StoredScan(long Id, Guid ScanId, string Root, ScanState State, DateTime CompletedUtc, long Files,
    long LogicalBytes, long ExcludedByPolicy, long Inaccessible, long UnvisitedDirectories, bool StoppedEarly);

// Scan results in cache.db: one row in scans, the largest files in top_files, paths interned as a tree.
public sealed class ScanStore(DatabaseWriter cache)
{
    public Task<long> SaveAsync(ScanResult result, DateTime completedUtc, CancellationToken cancellation = default) =>
        cache.ExecuteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            var paths = new PathTree(connection, transaction);
            long scanId;
            using (SqliteCommand insert = Command(connection, transaction, """
                INSERT INTO scans(scan_guid, root_path_id, completed_utc, state, mode, files, logical_bytes,
                    excluded_by_policy, inaccessible, unvisited, stopped_early, error_count)
                VALUES($guid, $root, $completed, $state, 'full', $files, $logical, $excluded, $inaccessible, $unvisited, $stopped, $errors)
                RETURNING id
                """))
            {
                insert.Parameters.AddWithValue("$guid", result.ScanId.ToString("D"));
                insert.Parameters.AddWithValue("$root", paths.Intern(result.Root));
                insert.Parameters.AddWithValue("$completed", Utc(completedUtc));
                insert.Parameters.AddWithValue("$state", StateName(result.State));
                insert.Parameters.AddWithValue("$files", result.Files);
                insert.Parameters.AddWithValue("$logical", result.LogicalBytes);
                insert.Parameters.AddWithValue("$excluded", result.ExcludedByPolicy);
                insert.Parameters.AddWithValue("$inaccessible", result.Inaccessible);
                insert.Parameters.AddWithValue("$unvisited", result.UnvisitedDirectories);
                insert.Parameters.AddWithValue("$stopped", result.StoppedEarly);
                insert.Parameters.AddWithValue("$errors", result.Errors);
                scanId = (long)insert.ExecuteScalar()!;
            }
            using (SqliteCommand top = Command(connection, transaction, """
                INSERT INTO top_files(scan_id, path_id, file_id, logical_bytes, allocated_bytes, links)
                VALUES($scan, $path, $id, $logical, $allocated, $links)
                """))
            {
                SqliteParameter path = top.Parameters.Add("$path", SqliteType.Integer), id = top.Parameters.Add("$id", SqliteType.Blob),
                    logical = top.Parameters.Add("$logical", SqliteType.Integer), allocated = top.Parameters.Add("$allocated", SqliteType.Integer),
                    links = top.Parameters.Add("$links", SqliteType.Integer);
                top.Parameters.AddWithValue("$scan", scanId);
                foreach (FileFinding file in result.LargestFiles)
                {
                    path.Value = paths.Intern(file.Path);
                    id.Value = file.Identity is { } identity ? Encode(identity) : DBNull.Value;
                    logical.Value = file.LogicalBytes;
                    allocated.Value = (object?)file.AllocatedBytes ?? DBNull.Value;
                    links.Value = (object?)file.Links ?? DBNull.Value;
                    top.ExecuteNonQuery();
                }
            }
            transaction.Commit();
            return scanId;
        }, cancellation);

    public const int ScansKept = 5;

    // NFR-PERF-006: only the newest scans stay. Their rows cascade (top files, aggregates, findings); path nodes that nothing
    // references and that have no children are removed leaf by leaf. Returns the number of scans removed.
    public Task<int> TrimAsync(int keep = ScansKept, CancellationToken cancellation = default) =>
        cache.ExecuteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            int removed;
            using (SqliteCommand delete = Command(connection, transaction,
                "DELETE FROM scans WHERE id NOT IN (SELECT id FROM scans ORDER BY id DESC LIMIT $keep)"))
            {
                delete.Parameters.AddWithValue("$keep", keep);
                removed = delete.ExecuteNonQuery();
            }
            if (removed > 0)
                while (true)
                {
                    using SqliteCommand prune = Command(connection, transaction, """
                        DELETE FROM paths WHERE
                            NOT EXISTS (SELECT 1 FROM scans WHERE root_path_id = paths.id)
                            AND NOT EXISTS (SELECT 1 FROM top_files WHERE path_id = paths.id)
                            AND NOT EXISTS (SELECT 1 FROM folder_agg WHERE path_id = paths.id)
                            AND NOT EXISTS (SELECT 1 FROM findings WHERE path_id = paths.id)
                            AND NOT EXISTS (SELECT 1 FROM paths child WHERE child.parent_id = paths.id)
                        """);
                    if (prune.ExecuteNonQuery() == 0) break;
                }
            transaction.Commit();
            return removed;
        }, cancellation);

    public Task<IReadOnlyList<StoredScan>> RecentAsync(int count, CancellationToken cancellation = default) =>
        cache.ExecuteAsync<IReadOnlyList<StoredScan>>(connection =>
        {
            using SqliteCommand query = Command(connection, null, """
                SELECT id, scan_guid, root_path_id, state, completed_utc,
                    files, logical_bytes, excluded_by_policy, inaccessible, unvisited, stopped_early
                FROM scans ORDER BY id DESC LIMIT $count
                """);
            query.Parameters.AddWithValue("$count", count);
            var scans = new List<StoredScan>();
            using SqliteDataReader row = query.ExecuteReader();
            while (row.Read())
                scans.Add(new(row.GetInt64(0), Guid.Parse(row.GetString(1)), PathTree.PathOf(connection, row.GetInt64(2)), ParseState(row.GetString(3)),
                    DateTime.Parse(row.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    row.GetInt64(5), row.GetInt64(6), row.GetInt64(7), row.GetInt64(8), row.GetInt64(9), row.GetBoolean(10)));
            return scans;
        }, cancellation);

    public Task<IReadOnlyList<FileFinding>> TopFilesAsync(long scanId, CancellationToken cancellation = default) =>
        cache.ExecuteAsync<IReadOnlyList<FileFinding>>(connection =>
        {
            using SqliteCommand query = Command(connection, null, """
                SELECT path_id, logical_bytes, allocated_bytes, file_id, links
                FROM top_files WHERE scan_id = $scan ORDER BY logical_bytes DESC, rowid
                """);
            query.Parameters.AddWithValue("$scan", scanId);
            var files = new List<FileFinding>();
            using SqliteDataReader row = query.ExecuteReader();
            while (row.Read())
                files.Add(new(PathTree.PathOf(connection, row.GetInt64(0)), row.GetInt64(1), row.IsDBNull(2) ? null : row.GetInt64(2),
                    row.IsDBNull(3) ? null : Decode((byte[])row.GetValue(3)), row.IsDBNull(4) ? null : row.GetInt32(4)));
            return files;
        }, cancellation);

    // file_id: 8-byte volume serial, then the 16-byte file ID, little-endian.
    private static byte[] Encode(FileIdentity identity)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, identity.VolumeSerial);
        BinaryPrimitives.WriteUInt128LittleEndian(bytes.AsSpan(8), identity.FileId);
        return bytes;
    }

    private static FileIdentity Decode(byte[] bytes) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(8)));

    private static string Utc(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string StateName(ScanState state) => state switch
    {
        ScanState.Completed => "completed",
        ScanState.Partial => "partial",
        ScanState.Cancelled => "cancelled",
        ScanState.Failed => "failed",
        ScanState.RootNotFound => "root_not_found",
        _ => throw new ArgumentException($"Скан в состоянии {state} не сохраняется.", nameof(state)),
    };

    private static ScanState ParseState(string name) => name switch
    {
        "completed" => ScanState.Completed,
        "partial" => ScanState.Partial,
        "cancelled" => ScanState.Cancelled,
        "root_not_found" => ScanState.RootNotFound,
        _ => ScanState.Failed,
    };

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    // Each path segment is a row keyed by (parent_id, name); a root segment has parent_id 0.
    private sealed class PathTree(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Walks up from the node to its root segment.
        public static string PathOf(SqliteConnection connection, long id)
        {
            using SqliteCommand query = Command(connection, null, """
                WITH RECURSIVE up(full, parent) AS (
                    SELECT name, parent_id FROM paths WHERE id = $id
                    UNION ALL
                    SELECT p.name || '\' || up.full, p.parent_id FROM up JOIN paths p ON p.id = up.parent)
                SELECT full FROM up WHERE parent = 0
                """);
            query.Parameters.AddWithValue("$id", id);
            return query.ExecuteScalar() as string ?? throw new InvalidDataException($"Путь {id} не найден в paths.");
        }

        private readonly Dictionary<(long, string), long> known = [];

        public long Intern(string path)
        {
            long parent = 0;
            foreach (string segment in Segments(path))
            {
                if (!known.TryGetValue((parent, segment), out long id))
                {
                    using SqliteCommand upsert = Command(connection, transaction, """
                        INSERT INTO paths(parent_id, name) VALUES($parent, $name)
                        ON CONFLICT(parent_id, name) DO UPDATE SET name = excluded.name
                        RETURNING id
                        """);
                    upsert.Parameters.AddWithValue("$parent", parent);
                    upsert.Parameters.AddWithValue("$name", segment);
                    id = (long)upsert.ExecuteScalar()!;
                    known[(parent, segment)] = id;
                }
                parent = id;
            }
            return parent;
        }

        // "C:\a\b" -> "C:", "a", "b"; "\\server\share\a" -> "\\server", "share", "a".
        private static IEnumerable<string> Segments(string path)
        {
            bool unc = path.StartsWith(@"\\", StringComparison.Ordinal);
            string[] parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw new ArgumentException("Пустой путь.", nameof(path));
            if (unc) parts[0] = @"\\" + parts[0];
            return parts;
        }
    }
}
