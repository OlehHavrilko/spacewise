using System.Globalization;
using Spacewise.Core;

namespace Spacewise.Data;

public sealed class FolderHistoryStore(DatabaseWriter cache)
{
    public Task SaveAsync(ScanResult scan, DateTime utc, CancellationToken cancellation = default)
    {
        if (scan.State != ScanState.Completed || scan.FolderAggregationTruncated) return Task.CompletedTask;
        return cache.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM folder_history WHERE taken_utc < $cutoff";
            command.Parameters.AddWithValue("$cutoff", utc.Date.AddDays(-30).ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery(); command.Parameters.Clear();
            command.CommandText = "DELETE FROM folder_history WHERE root=$root AND taken_utc=$day";
            command.Parameters.AddWithValue("$root", scan.Root);
            command.Parameters.AddWithValue("$day", utc.Date.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO folder_history(root,path,taken_utc,observed_utc,logical_bytes,files) VALUES($root,$path,$day,$observed,$bytes,$files)";
            var path = command.Parameters.AddWithValue("$path", "");
            command.Parameters.AddWithValue("$observed", utc.ToString("O", CultureInfo.InvariantCulture));
            var bytes = command.Parameters.AddWithValue("$bytes", 0L);
            var files = command.Parameters.AddWithValue("$files", 0L);
            foreach (FolderAggregate folder in new[] { new FolderAggregate(scan.Root, scan.Files, scan.LogicalBytes) }.Concat(scan.Folders.Take(64)))
            {
                path.Value = folder.Path; bytes.Value = folder.LogicalBytes; files.Value = folder.Files; command.ExecuteNonQuery();
            }
            command.Parameters.Clear();
            command.CommandText = "DELETE FROM folder_history WHERE root NOT IN (SELECT root FROM folder_history GROUP BY root ORDER BY max(taken_utc) DESC LIMIT 8)";
            command.ExecuteNonQuery();
            transaction.Commit();
            return 0;
        }, cancellation);
    }
    public Task<IReadOnlyList<string>> PathsAsync(CancellationToken cancellation = default) => cache.ExecuteAsync<IReadOnlyList<string>>(connection =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT DISTINCT path FROM folder_history ORDER BY path LIMIT 1024";
        using var reader = command.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }, cancellation);
    public Task<IReadOnlyList<FolderTrendPoint>> PointsAsync(string path, CancellationToken cancellation = default) => cache.ExecuteAsync<IReadOnlyList<FolderTrendPoint>>(connection =>
    {
        // Overlapping scan roots can observe the same path; retain one daily aggregate.
        using var command = connection.CreateCommand(); command.CommandText = "SELECT taken_utc,logical_bytes FROM (SELECT taken_utc,logical_bytes,row_number() OVER (PARTITION BY taken_utc ORDER BY observed_utc DESC) AS rank FROM folder_history WHERE path=$path) WHERE rank=1 ORDER BY taken_utc";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader(); var result = new List<FolderTrendPoint>();
        while (reader.Read()) result.Add(new(DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.GetInt64(1)));
        return result;
    }, cancellation);
}
