using System.Globalization;
using Microsoft.Data.Sqlite;
using Spacewise.Core;

namespace Spacewise.Data;

// Writes what the executor did into journal.db: item states, one actions row per attempted item, final plan state.
public sealed class ExecutionStore(DatabaseWriter journal)
{
    public static readonly TimeSpan DefaultQuarantineRetention = TimeSpan.FromDays(30);

    // Whole report at once (used when the executor ran without a write-ahead journal).
    public Task RecordAsync(long planId, ExecutionReport report, DateTime completedUtc, TimeSpan? quarantineRetention = null,
        CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            int seq = 0;
            foreach (ItemOutcome outcome in report.Outcomes)
                WriteOutcome(connection, transaction, planId, seq++, outcome, completedUtc, quarantineRetention ?? DefaultQuarantineRetention);
            SetPlanState(connection, transaction, planId, report.Cancelled ? "cancelled" : "done");
            transaction.Commit();
            return 0;
        }, cancellation);

    // Before an item is touched: a crash now leaves it "in_progress" for recovery to settle.
    public Task BeginItemAsync(long planId, int seq, CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Exec(connection, transaction, "UPDATE plan_items SET state = 'in_progress' WHERE plan_id = $plan AND seq = $seq", ("$plan", planId), ("$seq", seq));
            SetPlanState(connection, transaction, planId, "running");
            transaction.Commit();
            return 0;
        }, cancellation);

    public Task CompleteItemAsync(long planId, int seq, ItemOutcome outcome, DateTime atUtc, TimeSpan? quarantineRetention = null,
        CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            WriteOutcome(connection, transaction, planId, seq, outcome, atUtc, quarantineRetention ?? DefaultQuarantineRetention);
            transaction.Commit();
            return 0;
        }, cancellation);

    public Task FinishPlanAsync(long planId, string state, CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            SetPlanState(connection, transaction, planId, state);
            transaction.Commit();
            return 0;
        }, cancellation);

    // Blocking adapter for the synchronous executor; call it from a worker thread, never from the writer's own thread.
    public IExecutionJournal JournalFor(long planId, TimeProvider time) => new PlanJournal(this, planId, time);

    private sealed class PlanJournal(ExecutionStore store, long planId, TimeProvider time) : IExecutionJournal
    {
        public void Begin(int seq, PlanItem item) => store.BeginItemAsync(planId, seq).GetAwaiter().GetResult();
        public void Complete(int seq, ItemOutcome outcome) =>
            store.CompleteItemAsync(planId, seq, outcome, time.GetUtcNow().UtcDateTime).GetAwaiter().GetResult();
    }

    public sealed record InterruptedItem(long PlanId, int Seq);

    public Task<IReadOnlyList<InterruptedItem>> InterruptedAsync(CancellationToken cancellation = default) =>
        journal.ExecuteAsync<IReadOnlyList<InterruptedItem>>(connection =>
        {
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = "SELECT plan_id, seq FROM plan_items WHERE state = 'in_progress' ORDER BY plan_id, seq";
            using SqliteDataReader r = query.ExecuteReader();
            var list = new List<InterruptedItem>();
            while (r.Read()) list.Add(new(r.GetInt64(0), (int)r.GetInt64(1)));
            return list;
        }, cancellation);

    // Plans whose run never finished (state "running") are closed as "interrupted" once their items are settled.
    public Task<int> CloseUnfinishedPlansAsync(CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteCommand update = connection.CreateCommand();
            update.CommandText = "UPDATE plans SET state = 'interrupted' WHERE state = 'running' AND NOT EXISTS (SELECT 1 FROM plan_items WHERE plan_id = plans.id AND state = 'in_progress')";
            return update.ExecuteNonQuery();
        }, cancellation);

    public Task<IReadOnlyList<string>> KnownStoredPathsAsync(CancellationToken cancellation = default) =>
        journal.ExecuteAsync<IReadOnlyList<string>>(connection =>
        {
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = "SELECT stored_path_enc FROM quarantine";
            using SqliteDataReader r = query.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(System.Text.Encoding.UTF8.GetString((byte[])r.GetValue(0)));
            return list;
        }, cancellation);

    private static void SetPlanState(SqliteConnection connection, SqliteTransaction transaction, long planId, string state) =>
        Exec(connection, transaction, "UPDATE plans SET state = $state WHERE id = $plan", ("$state", state), ("$plan", planId));

    private static void Exec(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    // One item's result: plan_items state, actions row, decision row and, if it was quarantined, the quarantine row. One transaction.
    // sha256 comes from the remover (null if unreadable).
    private static void WriteOutcome(SqliteConnection connection, SqliteTransaction transaction, long planId, int seq, ItemOutcome outcome,
        DateTime atUtc, TimeSpan retention)
    {
        bool removed = outcome.Status == RemoveStatus.Removed;
        string at = atUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        Exec(connection, transaction, "UPDATE plan_items SET state = $state, skip_reason = $reason WHERE plan_id = $plan AND seq = $seq",
            ("$state", removed ? "done" : outcome.Status == RemoveStatus.Failed ? "failed" : "skipped"),
            ("$reason", removed ? null : outcome.Status.ToString()), ("$plan", planId), ("$seq", seq));
        // Logical size from the plan; real freed space is measured separately (FR-CLN-002).
        Exec(connection, transaction, """
            INSERT INTO actions(plan_id, seq, started_utc, completed_utc, result_code, bytes_planned, bytes_freed_measured, native_reported)
            VALUES($plan, $seq, $at, $at, $code, $planned, NULL, $reported)
            """, ("$plan", planId), ("$seq", seq), ("$at", at), ("$code", outcome.Code), ("$planned", outcome.Item.ExpectedSize),
            ("$reported", removed ? outcome.Item.ExpectedSize : 0));
        DecisionJournal.Append(connection, transaction, atUtc, new("user", "manual", $"{outcome.Item.RuleId}@{outcome.Item.RuleVersion}",
            outcome.Item.Path, outcome.Item.ExpectedSize, outcome.Item.RuleId, [outcome.Item.RuleId], "approve", outcome.Status.ToString(), outcome.StoredPath));
        if (removed && outcome.StoredPath is { } stored)
            Exec(connection, transaction, """
                INSERT INTO quarantine(plan_id, seq, original_path_enc, stored_path_enc, sha256, size, created_utc, expires_utc)
                VALUES($plan, $seq, $original, $stored, $sha, $size, $at, $expires)
                """, ("$plan", planId), ("$seq", seq), ("$original", System.Text.Encoding.UTF8.GetBytes(outcome.Item.Path)),
                ("$stored", System.Text.Encoding.UTF8.GetBytes(stored)), ("$sha", outcome.Sha256), ("$size", outcome.Item.ExpectedSize),
                ("$at", at), ("$expires", (atUtc + retention).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
    }

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
            using SqliteTransaction transaction = connection.BeginTransaction();
            update.Transaction = transaction;
            update.CommandText = "UPDATE quarantine SET restored_utc = $at WHERE id = $id";
            update.Parameters.AddWithValue("$at", restoredUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", quarantineId);
            int rows = update.ExecuteNonQuery();
            LogQuarantineEvent(connection, transaction, quarantineId, restoredUtc, "restore");
            transaction.Commit();
            return rows;
        }, cancellation);

    public Task MarkPurgedAsync(long quarantineId, DateTime purgedUtc, CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteCommand update = connection.CreateCommand();
            using SqliteTransaction transaction = connection.BeginTransaction();
            update.Transaction = transaction;
            update.CommandText = "UPDATE quarantine SET purged_utc = $at WHERE id = $id";
            update.Parameters.AddWithValue("$at", purgedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", quarantineId);
            int rows = update.ExecuteNonQuery();
            LogQuarantineEvent(connection, transaction, quarantineId, purgedUtc, "purge");
            transaction.Commit();
            return rows;
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

    // FR-DATA-002: only quarantine records of already purged files older than 90 days are ever trimmed.
    public static readonly TimeSpan PurgedRecordRetention = TimeSpan.FromDays(90);

    public Task<int> TrimPurgedRecordsAsync(DateTime nowUtc, CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteCommand delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM quarantine WHERE purged_utc IS NOT NULL AND purged_utc < $cutoff";
            delete.Parameters.AddWithValue("$cutoff", (nowUtc - PurgedRecordRetention).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            return delete.ExecuteNonQuery();
        }, cancellation);

    public sealed record DecisionRow(long Id, DateTime TimeUtc, string Action, string Result, string Category, long Bytes, string Path);

    // Newest first, for the "История" tab.
    public Task<IReadOnlyList<DecisionRow>> RecentDecisionsAsync(int limit, CancellationToken cancellation = default) =>
        journal.ExecuteAsync<IReadOnlyList<DecisionRow>>(connection =>
        {
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = "SELECT id, ts, user_action, executor_result, category, bytes, path_enc FROM decision_journal ORDER BY id DESC LIMIT $limit";
            query.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader r = query.ExecuteReader();
            var list = new List<DecisionRow>();
            while (r.Read())
                list.Add(new(r.GetInt64(0), DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                    r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), System.Text.Encoding.UTF8.GetString((byte[])r.GetValue(6))));
            return list;
        }, cancellation);

    public Task<string?> GetSettingAsync(string key, CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = "SELECT value_json FROM settings WHERE key = $key";
            query.Parameters.AddWithValue("$key", key);
            return query.ExecuteScalar() as string;
        }, cancellation);

    // Null when the decision journal chain is intact, otherwise the first broken row id.
    public Task<long?> VerifyJournalAsync(CancellationToken cancellation = default) =>
        journal.ExecuteAsync(DecisionJournal.FirstBrokenRow, cancellation);

    private static void LogQuarantineEvent(SqliteConnection connection, SqliteTransaction transaction, long id, DateTime utc, string action)
    {
        using SqliteCommand read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT original_path_enc, stored_path_enc, size FROM quarantine WHERE id = $id";
        read.Parameters.AddWithValue("$id", id);
        using SqliteDataReader r = read.ExecuteReader();
        if (!r.Read()) return;
        string original = System.Text.Encoding.UTF8.GetString((byte[])r.GetValue(0)), stored = System.Text.Encoding.UTF8.GetString((byte[])r.GetValue(1));
        long size = r.GetInt64(2);
        r.Close();
        DecisionJournal.Append(connection, transaction, utc, new("user", "manual", "quarantine", original, size, "quarantine", [], action, "Done", stored));
    }
}
