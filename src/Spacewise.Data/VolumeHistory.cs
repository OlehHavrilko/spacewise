using System.Globalization;
using Microsoft.Data.Sqlite;
using Spacewise.Core;

namespace Spacewise.Data;

// Volume snapshots in cache.db (spec §5.4): aggregates only, one row per volume and visit. They are what the forecast is built from.
public sealed class VolumeHistory(DatabaseWriter cache)
{
    // A snapshot closer than this to the previous one adds no information and is skipped.
    public static readonly TimeSpan MinimumGap = TimeSpan.FromHours(1);

    public Task RecordAsync(IEnumerable<VolumeInfo> volumes, DateTime nowUtc, string trigger = "startup", CancellationToken cancellation = default) =>
        cache.ExecuteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (VolumeInfo volume in volumes)
            {
                long id = VolumeId(connection, transaction, volume, nowUtc);
                using (SqliteCommand last = Cmd(connection, transaction, "SELECT max(taken_utc) FROM volume_snapshot WHERE volume_id = $id"))
                {
                    last.Parameters.AddWithValue("$id", id);
                    if (last.ExecuteScalar() is string previous
                        && nowUtc - DateTime.Parse(previous, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime() < MinimumGap)
                        continue;
                }
                using SqliteCommand insert = Cmd(connection, transaction,
                    "INSERT INTO volume_snapshot(volume_id, taken_utc, total_bytes, free_bytes, available_bytes, trigger) VALUES($id, $at, $total, $free, $free, $trigger)");
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$at", nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$total", volume.TotalBytes);
                insert.Parameters.AddWithValue("$free", volume.FreeBytes);
                insert.Parameters.AddWithValue("$trigger", trigger);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return 0;
        }, cancellation);

    public Task<IReadOnlyList<SpacePoint>> PointsAsync(string volumeGuid, CancellationToken cancellation = default) =>
        cache.ExecuteAsync<IReadOnlyList<SpacePoint>>(connection =>
        {
            using SqliteCommand query = Cmd(connection, null, """
                SELECT s.taken_utc, s.free_bytes FROM volume_snapshot s JOIN volumes v ON v.id = s.volume_id
                WHERE v.volume_guid = $guid ORDER BY s.taken_utc
                """);
            query.Parameters.AddWithValue("$guid", volumeGuid);
            using SqliteDataReader r = query.ExecuteReader();
            var list = new List<SpacePoint>();
            while (r.Read())
                list.Add(new(DateTime.Parse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(), r.GetInt64(1)));
            return list;
        }, cancellation);

    // "Стереть историю": usage data and volume history are the only personal-ish aggregates kept in cache.db.
    public Task<int> EraseAsync(CancellationToken cancellation = default) =>
        cache.ExecuteAsync(connection =>
        {
            using SqliteCommand delete = Cmd(connection, null, "DELETE FROM volume_snapshot");
            return delete.ExecuteNonQuery();
        }, cancellation);

    private static long VolumeId(SqliteConnection connection, SqliteTransaction transaction, VolumeInfo volume, DateTime nowUtc)
    {
        using SqliteCommand upsert = Cmd(connection, transaction, """
            INSERT INTO volumes(volume_guid, fs, label, last_seen_utc) VALUES($guid, $fs, $label, $at)
            ON CONFLICT(volume_guid) DO UPDATE SET fs = excluded.fs, label = excluded.label, last_seen_utc = excluded.last_seen_utc
            RETURNING id
            """);
        upsert.Parameters.AddWithValue("$guid", volume.VolumeGuid);
        upsert.Parameters.AddWithValue("$fs", volume.FileSystem);
        upsert.Parameters.AddWithValue("$label", volume.Label);
        upsert.Parameters.AddWithValue("$at", nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return (long)upsert.ExecuteScalar()!;
    }

    private static SqliteCommand Cmd(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
