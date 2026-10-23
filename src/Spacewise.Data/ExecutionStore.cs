using System.Globalization;
using Microsoft.Data.Sqlite;
using Spacewise.Core;

namespace Spacewise.Data;

// Writes what the executor did into journal.db: item states, one actions row per attempted item, final plan state.
public sealed class ExecutionStore(DatabaseWriter journal)
{
    public static readonly TimeSpan DefaultQuarantineRetention = TimeSpan.FromDays(30);

    // Quarantined items get a row with both paths so they can be restored; sha256 comes from the remover (null if unreadable).
    public Task RecordAsync(long planId, ExecutionReport report, DateTime completedUtc, TimeSpan? quarantineRetention = null,
        CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand item = connection.CreateCommand(), action = connection.CreateCommand(), plan = connection.CreateCommand(), hold = connection.CreateCommand();
            item.Transaction = action.Transaction = plan.Transaction = hold.Transaction = transaction;
            hold.CommandText = """
                INSERT INTO quarantine(plan_id, seq, original_path_enc, stored_path_enc, sha256, size, created_utc, expires_utc)
                VALUES($plan, $seq, $original, $stored, $sha, $size, $at, $expires)
                """;
            string expires = (completedUtc + (quarantineRetention ?? DefaultQuarantineRetention)).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            item.CommandText = "UPDATE plan_items SET state = $state, skip_reason = $reason WHERE plan_id = $plan AND seq = $seq";
            action.CommandText = """
                INSERT INTO actions(plan_id, seq, started_utc, completed_utc, result_code, bytes_planned, bytes_freed_measured, native_reported)
                VALUES($plan, $seq, $at, $at, $code, $planned, NULL, $reported)
                """;
            plan.CommandText = "UPDATE plans SET state = $state WHERE id = $plan";
            string at = completedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            item.Parameters.AddWithValue("$plan", planId);
            action.Parameters.AddWithValue("$plan", planId);
            action.Parameters.AddWithValue("$at", at);
            SqliteParameter state = item.Parameters.Add("$state", SqliteType.Text), reason = item.Parameters.Add("$reason", SqliteType.Text),
                itemSeq = item.Parameters.Add("$seq", SqliteType.Integer), actionSeq = action.Parameters.Add("$seq", SqliteType.Integer),
                code = action.Parameters.Add("$code", SqliteType.Integer), planned = action.Parameters.Add("$planned", SqliteType.Integer),
                reported = action.Parameters.Add("$reported", SqliteType.Integer);
            int seq = 0;
            foreach (ItemOutcome outcome in report.Outcomes)
            {
                bool removed = outcome.Status == RemoveStatus.Removed;
                itemSeq.Value = actionSeq.Value = seq++;
                state.Value = removed ? "done" : outcome.Status == RemoveStatus.Failed ? "failed" : "skipped";
                reason.Value = removed ? DBNull.Value : outcome.Status.ToString();
                item.ExecuteNonQuery();
                code.Value = outcome.Code;
                planned.Value = outcome.Item.ExpectedSize;
                // Logical size from the plan; real freed space is measured separately (FR-CLN-002).
                reported.Value = removed ? outcome.Item.ExpectedSize : 0;
                action.ExecuteNonQuery();
                if (removed && outcome.StoredPath is { } stored)
                {
                    hold.Parameters.Clear();
                    hold.Parameters.AddWithValue("$plan", planId);
                    hold.Parameters.AddWithValue("$seq", seq - 1);
                    hold.Parameters.AddWithValue("$original", System.Text.Encoding.UTF8.GetBytes(outcome.Item.Path));
                    hold.Parameters.AddWithValue("$stored", System.Text.Encoding.UTF8.GetBytes(stored));
                    hold.Parameters.AddWithValue("$sha", (object?)outcome.Sha256 ?? DBNull.Value);
                    hold.Parameters.AddWithValue("$size", outcome.Item.ExpectedSize);
                    hold.Parameters.AddWithValue("$at", at);
                    hold.Parameters.AddWithValue("$expires", expires);
                    hold.ExecuteNonQuery();
                }
            }
            plan.Parameters.AddWithValue("$plan", planId);
            plan.Parameters.AddWithValue("$state", report.Cancelled ? "cancelled" : "done");
            plan.ExecuteNonQuery();
            transaction.Commit();
            return 0;
        }, cancellation);

    public sealed record QuarantinedFile(long Id, string OriginalPath, string StoredPath, long Size, DateTime ExpiresUtc, byte[]? Sha256);

    // Everything still held in quarantine: not restored, not purged. Works across sessions.
    public Task<IReadOnlyList<QuarantinedFile>> HeldAsync(CancellationToken cancellation = default) =>
        journal.ExecuteAsync<IReadOnlyList<QuarantinedFile>>(connection =>
        {
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = "SELECT id, original_path_enc, stored_path_enc, size, expires_utc, sha256 FROM quarantine WHERE restored_utc IS NULL AND purged_utc IS NULL ORDER BY id";
            using SqliteDataReader r = query.ExecuteReader();
            var list = new List<QuarantinedFile>();
            while (r.Read())
                list.Add(new(r.GetInt64(0), System.Text.Encoding.UTF8.GetString((byte[])r.GetValue(1)),
                    System.Text.Encoding.UTF8.GetString((byte[])r.GetValue(2)), r.GetInt64(3),
                    DateTime.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                    r.IsDBNull(5) ? null : (byte[])r.GetValue(5)));
            return list;
        }, cancellation);

    public Task MarkRestoredAsync(long quarantineId, DateTime restoredUtc, CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteCommand update = connection.CreateCommand();
            update.CommandText = "UPDATE quarantine SET restored_utc = $at WHERE id = $id";
            update.Parameters.AddWithValue("$at", restoredUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", quarantineId);
            return update.ExecuteNonQuery();
        }, cancellation);

    public Task MarkPurgedAsync(long quarantineId, DateTime purgedUtc, CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteCommand update = connection.CreateCommand();
            update.CommandText = "UPDATE quarantine SET purged_utc = $at WHERE id = $id";
            update.Parameters.AddWithValue("$at", purgedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", quarantineId);
            return update.ExecuteNonQuery();
        }, cancellation);

    public Task SaveSettingAsync(string key, string valueJson, CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteCommand upsert = connection.CreateCommand();
            upsert.CommandText = "INSERT INTO settings(key, value_json) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json";
            upsert.Parameters.AddWithValue("$key", key);
            upsert.Parameters.AddWithValue("$value", valueJson);
            return upsert.ExecuteNonQuery();
        }, cancellation);
}
