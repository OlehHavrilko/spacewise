using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Spacewise.Data;

public sealed record DecisionEntry(string Actor, string Mode, string FactId, string Path, long Bytes, string Category,
    IReadOnlyList<string> PolicyRuleIds, string UserAction, string ExecutorResult, string? UndoToken);

// FR-AI-007: every decision row carries the hash of the previous row, so editing or deleting a row breaks the chain.
// The journal is encrypted as a whole (ADR 0005); the chain protects against silent edits, not against a hostile writer.
public static class DecisionJournal
{
    // Must run inside the caller's transaction on the single journal writer, so prev_hash is read and written atomically.
    public static void Append(SqliteConnection connection, SqliteTransaction? transaction, DateTime utc, DecisionEntry entry)
    {
        byte[] previous = LastHash(connection, transaction);
        string ts = utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        byte[] pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(entry.Path));
        string rules = JsonSerializer.Serialize(entry.PolicyRuleIds);
        byte[] rowHash = RowHash(previous, ts, entry, pathHash, rules);
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO decision_journal(ts, actor, mode, fact_id, path_hash, path_enc, bytes, category, policy_rule_ids_json,
                user_action, executor_result, undo_token, prev_hash, row_hash)
            VALUES($ts, $actor, $mode, $fact, $pathHash, $path, $bytes, $category, $rules, $action, $result, $undo, $prev, $row)
            """;
        insert.Parameters.AddWithValue("$ts", ts);
        insert.Parameters.AddWithValue("$actor", entry.Actor);
        insert.Parameters.AddWithValue("$mode", entry.Mode);
        insert.Parameters.AddWithValue("$fact", entry.FactId);
        insert.Parameters.AddWithValue("$pathHash", pathHash);
        insert.Parameters.AddWithValue("$path", Encoding.UTF8.GetBytes(entry.Path));
        insert.Parameters.AddWithValue("$bytes", entry.Bytes);
        insert.Parameters.AddWithValue("$category", entry.Category);
        insert.Parameters.AddWithValue("$rules", rules);
        insert.Parameters.AddWithValue("$action", entry.UserAction);
        insert.Parameters.AddWithValue("$result", entry.ExecutorResult);
        insert.Parameters.AddWithValue("$undo", (object?)entry.UndoToken ?? DBNull.Value);
        insert.Parameters.AddWithValue("$prev", previous);
        insert.Parameters.AddWithValue("$row", rowHash);
        insert.ExecuteNonQuery();
    }

    // Null when the chain is intact, otherwise the id of the first row that does not match its hashes.
    public static long? FirstBrokenRow(SqliteConnection connection)
    {
        using SqliteCommand query = connection.CreateCommand();
        query.CommandText = """
            SELECT id, ts, actor, mode, fact_id, path_hash, path_enc, bytes, category, policy_rule_ids_json, user_action,
                executor_result, undo_token, prev_hash, row_hash FROM decision_journal ORDER BY id
            """;
        using SqliteDataReader r = query.ExecuteReader();
        byte[] expectedPrevious = [];
        while (r.Read())
        {
            string rules = r.GetString(9);
            var entry = new DecisionEntry(r.GetString(2), r.GetString(3), r.GetString(4), Encoding.UTF8.GetString((byte[])r.GetValue(6)),
                r.GetInt64(7), r.GetString(8), [], r.GetString(10), r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12));
            byte[] pathHash = SHA256.HashData((byte[])r.GetValue(6));
            byte[] previous = (byte[])r.GetValue(13), stored = (byte[])r.GetValue(14);
            if (!previous.AsSpan().SequenceEqual(expectedPrevious)
                || !pathHash.AsSpan().SequenceEqual((byte[])r.GetValue(5))
                || !RowHash(previous, r.GetString(1), entry, pathHash, rules).AsSpan().SequenceEqual(stored))
                return r.GetInt64(0);
            expectedPrevious = stored;
        }
        return null;
    }

    private static byte[] LastHash(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT row_hash FROM decision_journal ORDER BY id DESC LIMIT 1";
        return query.ExecuteScalar() as byte[] ?? [];
    }

    // Length-prefixed fields: no boundary can be shifted to produce the same bytes.
    private static byte[] RowHash(byte[] previous, string ts, DecisionEntry e, byte[] pathHash, string rules)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(byte[] data) { hash.AppendData(BitConverter.GetBytes(data.Length)); hash.AppendData(data); }
        void AddText(string? text) => Add(Encoding.UTF8.GetBytes(text ?? "\0"));
        Add(previous);
        AddText(ts); AddText(e.Actor); AddText(e.Mode); AddText(e.FactId);
        Add(pathHash);
        AddText(e.Bytes.ToString(CultureInfo.InvariantCulture)); AddText(e.Category); AddText(rules);
        AddText(e.UserAction); AddText(e.ExecutorResult); AddText(e.UndoToken);
        return hash.GetHashAndReset();
    }
}
