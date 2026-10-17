using Microsoft.Data.Sqlite;

namespace BilibiliLiveAssistant.Infrastructure;

public sealed partial class SqliteLocalHistory
{
    private static void CompactStoredJson(SqliteConnection connection, CancellationToken token)
    {
        // Fixed internal identifiers, never user SQL. Keyset batches bound memory
        // and transaction size; cancellation leaves already compacted rows valid.
        var tables = new (string Table, string[] Columns)[]
        {
            ("events", ["appearance", "fields"]),
            ("member_changes", ["before_data", "after_data"]),
            ("analysis_samples", ["data"]),
            ("snapshots", ["rank"]),
            ("profiles", ["data"]),
        };
        foreach (var (table, columns) in tables)
        {
            long after = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var rows = new List<(long Id, string?[] Values)>(128);
                long textBytes = 0;
                using (var select = Command(connection, $"SELECT id,{string.Join(',', columns)} FROM {table} WHERE id>$after ORDER BY id LIMIT 128", ("$after", after)))
                using (var reader = select.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var values = new string?[columns.Length];
                        for (var i = 0; i < values.Length; i++)
                        {
                            values[i] = reader.IsDBNull(i + 1) ? null : reader.GetString(i + 1);
                            textBytes += 2L * (values[i]?.Length ?? 0);
                        }
                        rows.Add((reader.GetInt64(0), values));
                        if (textBytes >= 2 * 1024 * 1024) break;
                    }
                }
                if (rows.Count == 0) break;
                using (var transaction = new HistoryTransaction(connection))
                using (var update = Command(connection, $"UPDATE {table} SET {string.Join(',', columns.Select((column, i) => $"{column}=$v{i}"))} WHERE id=$id"))
                {
                    update.Parameters.Add("$id", SqliteType.Integer);
                    for (var i = 0; i < columns.Length; i++) update.Parameters.Add($"$v{i}", SqliteType.Text);
                    foreach (var row in rows)
                    {
                        token.ThrowIfCancellationRequested();
                        var changed = false;
                        update.Parameters["$id"].Value = row.Id;
                        for (var i = 0; i < columns.Length; i++)
                        {
                            var old = row.Values[i];
                            var compact = old is null ? null : HistoryJson.CompactStored(old, table == "events" && columns[i] == "appearance");
                            changed |= old != compact;
                            update.Parameters[$"$v{i}"].Value = (object?)compact ?? DBNull.Value;
                        }
                        if (changed) update.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
                after = rows[^1].Id;
                // Maintenance owns the reader gate. Keep a whole-database JSON
                // rewrite from accumulating an equally large WAL beside it.
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
        }
    }
}
