using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Spacewise.Core;

namespace Spacewise.Data;

// Plans in journal.db. The database is encrypted as a whole (ADR 0005), so path_enc holds plain UTF-8.
public sealed class PlanStore(DatabaseWriter journal)
{
    public const string DraftState = "draft";

    public Task<long> SaveAsync(CleanupPlan plan, long? scanId = null, CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            long planId;
            using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO plans(created_utc, scan_id, digest, origin, state, elevated, expires_utc)
                    VALUES($created, $scan, $digest, $origin, $state, 0, $expires) RETURNING id
                    """;
                insert.Parameters.AddWithValue("$created", Utc(plan.CreatedUtc));
                insert.Parameters.AddWithValue("$scan", (object?)scanId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$digest", plan.Digest);
                insert.Parameters.AddWithValue("$origin", plan.Origin);
                insert.Parameters.AddWithValue("$state", DraftState);
                insert.Parameters.AddWithValue("$expires", Utc(plan.ExpiresUtc));
                planId = (long)insert.ExecuteScalar()!;
            }
            using (SqliteCommand item = connection.CreateCommand())
            {
                item.Transaction = transaction;
                item.CommandText = """
                    INSERT INTO plan_items(plan_id, seq, method, path_enc, expected_size, expected_mtime, rule_id, rule_version, action_id, state)
                    VALUES($plan, $seq, 'delete', $path, $size, $mtime, $rule, $version, $action, 'pending')
                    """;
                SqliteParameter seq = item.Parameters.Add("$seq", SqliteType.Integer), path = item.Parameters.Add("$path", SqliteType.Blob),
                    size = item.Parameters.Add("$size", SqliteType.Integer), mtime = item.Parameters.Add("$mtime", SqliteType.Text),
                    rule = item.Parameters.Add("$rule", SqliteType.Text), version = item.Parameters.Add("$version", SqliteType.Integer),
                    action = item.Parameters.Add("$action", SqliteType.Text);
                item.Parameters.AddWithValue("$plan", planId);
                int n = 0;
                foreach (PlanItem i in plan.Items)
                {
                    seq.Value = n++;
                    path.Value = Encoding.UTF8.GetBytes(i.Path);
                    size.Value = i.ExpectedSize;
                    mtime.Value = Utc(i.ExpectedMtimeUtc);
                    rule.Value = i.RuleId;
                    version.Value = i.RuleVersion;
                    action.Value = i.ActionId;
                    item.ExecuteNonQuery();
                }
            }
            transaction.Commit();
            return planId;
        }, cancellation);

    // Reads the plan back for the executor: the digest is recomputed, never trusted from the row.
    public Task<CleanupPlan?> LoadAsync(long planId, CancellationToken cancellation = default) =>
        journal.ExecuteAsync(connection =>
        {
            using SqliteCommand head = connection.CreateCommand();
            head.CommandText = "SELECT created_utc, expires_utc, origin, digest FROM plans WHERE id = $id";
            head.Parameters.AddWithValue("$id", planId);
            using SqliteDataReader r = head.ExecuteReader();
            if (!r.Read()) return null;
            DateTime created = Parse(r.GetString(0)), expires = Parse(r.GetString(1));
            string origin = r.GetString(2);
            byte[] stored = (byte[])r.GetValue(3);
            r.Close();

            var items = new List<PlanItem>();
            using SqliteCommand rows = connection.CreateCommand();
            rows.CommandText = "SELECT rule_id, rule_version, action_id, path_enc, expected_size, expected_mtime FROM plan_items WHERE plan_id = $id ORDER BY seq";
            rows.Parameters.AddWithValue("$id", planId);
            using SqliteDataReader ri = rows.ExecuteReader();
            while (ri.Read())
                items.Add(new(ri.GetString(0), (int)ri.GetInt64(1), ri.GetString(2), Encoding.UTF8.GetString((byte[])ri.GetValue(3)), ri.GetInt64(4), Parse(ri.GetString(5))));
            var plan = new CleanupPlan(created, expires, origin, items, stored);
            return plan.DigestMatches() ? plan : throw new InvalidDataException($"План {planId}: digest не совпал с позициями.");
        }, cancellation);

    private static string Utc(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime Parse(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
