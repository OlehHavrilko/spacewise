using System.Globalization;
using Microsoft.Data.Sqlite;
using Spacewise.Core;

namespace Spacewise.Data;

// Writes what the executor did into journal.db: item states, one actions row per attempted item, final plan state.
public sealed class ExecutionStore(DatabaseWriter journal)
{
    public Task RecordAsync(long planId, ExecutionReport report, DateTime completedUtc, CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand item = connection.CreateCommand(), action = connection.CreateCommand(), plan = connection.CreateCommand();
            item.Transaction = action.Transaction = plan.Transaction = transaction;
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
            }
            plan.Parameters.AddWithValue("$plan", planId);
            plan.Parameters.AddWithValue("$state", report.Cancelled ? "cancelled" : "done");
            plan.ExecuteNonQuery();
            transaction.Commit();
            return 0;
        }, cancellation);
}
