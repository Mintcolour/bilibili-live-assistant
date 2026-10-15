using BilibiliLiveAssistant.Core;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BilibiliLiveAssistant.Infrastructure;

public sealed partial class SqliteLocalHistory
{
    private void RequireStopped()
    {
        lock (_sync) if (_current is not null) throw new InvalidOperationException("请先停止监视，再执行存档维护");
    }
    private async Task MaintainAsync(Action<SqliteConnection> action, CancellationToken token)
    {
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        await _readGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await QueueCommand(connection =>
            {
                token.ThrowIfCancellationRequested();
                raw.sqlite3_progress_handler(connection.Handle, 1000, _ => token.IsCancellationRequested ? 1 : 0, null);
                try { _medalStates.Clear(); action(connection); }
                finally { raw.sqlite3_progress_handler(connection.Handle, 0, null, null); }
            }).ConfigureAwait(false);
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally { _readGate.Release(); }
        Publish(Status.Message, Status.Faulted); Committed?.Invoke();
    }
    public Task DeleteAsync(HistoryFilter filter, CancellationToken token) => MaintainAsync(connection =>
    {
        using (var active = connection.CreateCommand())
        {
            var where = Scope(filter, active, "s").Replace("s.local_day >= $from AND s.local_day <= $to", "s.last_day >= $from AND s.first_day <= $to");
            active.CommandText = $"SELECT COUNT(*) FROM sessions s WHERE {where} AND s.status='active'";
            if (Convert.ToInt64(active.ExecuteScalar()) > 0) throw new InvalidOperationException("清理范围包含正在记录的监视段，请先停止监视");
        }
        foreach (var table in new[] { "events", "profiles", "snapshots", "analysis_samples", "member_changes", "gaps", "summaries" })
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                using var transaction = new HistoryTransaction(connection); using var delete = connection.CreateCommand();
                var where = Scope(filter, delete);
                if (table == "events")
                {
                    delete.CommandText = $"""
                        SELECT session_id,local_day,type,COUNT(*),COALESCE(SUM(CASE WHEN type IN(2,4) AND paid=1 THEN amount ELSE 0 END),0),
                        COALESCE(SUM(CASE WHEN type IN(2,4) AND paid=0 THEN quantity ELSE 0 END),0),
                        COALESCE(SUM(CASE WHEN type=3 THEN amount ELSE 0 END),0),
                        SUM(CASE WHEN (type IN(2,4) AND (paid IS NULL OR paid<>0) OR type=3) AND amount IS NULL THEN 1 ELSE 0 END)
                        FROM events WHERE id IN(SELECT e.id FROM events e JOIN sessions s ON s.id=e.session_id WHERE {where} LIMIT 500)
                        GROUP BY session_id,local_day,type
                        """;
                    var groups = new List<(string Session, string Day, int Type, long Count, long Paid, long Free, long Sc, long Unknown)>();
                    using (var reader = delete.ExecuteReader()) while (reader.Read()) groups.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                        reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)));
                    foreach (var g in groups)
                        Execute(connection, "UPDATE summaries SET count=count-$n,paid_amount=paid_amount-$p,free_quantity=free_quantity-$f,sc_amount=sc_amount-$sc,unknown=unknown-$u WHERE session_id=$s AND local_day=$d AND type=$t",
                            ("$n", g.Count), ("$p", g.Paid), ("$f", g.Free), ("$sc", g.Sc), ("$u", g.Unknown), ("$s", g.Session), ("$d", g.Day), ("$t", g.Type));
                }
                delete.CommandText = $"DELETE FROM {table} WHERE rowid IN (SELECT e.rowid FROM {table} e JOIN sessions s ON s.id=e.session_id WHERE {where} LIMIT 500)";
                var count = delete.ExecuteNonQuery(); transaction.Commit(); if (count == 0) break;
            }
        }
        // Keep partially selected sessions; only entirely empty completed sessions can disappear.
        using var cleanup = connection.CreateCommand(); var scope = Scope(filter, cleanup, "s")
            .Replace("s.local_day >= $from AND s.local_day <= $to", "s.last_day >= $from AND s.first_day <= $to");
        cleanup.CommandText = $"""
            DELETE FROM sessions AS s WHERE {scope} AND status<>'active'
            AND NOT EXISTS(SELECT 1 FROM events WHERE session_id=s.id)
            AND NOT EXISTS(SELECT 1 FROM profiles WHERE session_id=s.id)
            AND NOT EXISTS(SELECT 1 FROM snapshots WHERE session_id=s.id)
            AND NOT EXISTS(SELECT 1 FROM analysis_samples WHERE session_id=s.id)
            AND NOT EXISTS(SELECT 1 FROM member_changes WHERE session_id=s.id)
            AND NOT EXISTS(SELECT 1 FROM gaps WHERE session_id=s.id)
            AND NOT EXISTS(SELECT 1 FROM summaries WHERE session_id=s.id)
            """;
        cleanup.ExecuteNonQuery();
        using var catalogue = new HistoryTransaction(connection);
        Execute(connection, "DELETE FROM room_aliases WHERE NOT EXISTS(SELECT 1 FROM sessions WHERE anchor_uid=room_aliases.uid);");
        Execute(connection, "DELETE FROM anchor_names WHERE NOT EXISTS(SELECT 1 FROM sessions WHERE anchor_uid=anchor_names.uid);");
        Execute(connection, "DELETE FROM anchors WHERE NOT EXISTS(SELECT 1 FROM sessions WHERE anchor_uid=anchors.uid);");
        catalogue.Commit();
    }, token);
    public Task CompactAsync(CancellationToken token)
    {
        RequireStopped(); return MaintainAsync(connection =>
        { RequireStopped(); Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);"); Execute(connection, "VACUUM;"); }, token);
    }
    public async Task MoveAsync(string directory, CancellationToken token)
    {
        RequireStopped();
        var targetDirectory = Path.GetFullPath(directory); var source = DatabasePath;
        var destination = Path.Combine(targetDirectory, "history.db");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
        await MaintainAsync(connection =>
        {
            RequireStopped(); Directory.CreateDirectory(targetDirectory);
            if (File.Exists(destination)) throw new IOException("目标目录已有 history.db，请选择空存档目录；不会覆盖现有数据库");
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                using (var target = Open(path: temporary))
                {
                    connection.BackupDatabase(target);
                    using var check = Command(target, "PRAGMA integrity_check;");
                    if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidDataException("复制的历史数据库校验失败");
                    using var version = Command(target, "PRAGMA user_version;");
                    if (Convert.ToInt32(version.ExecuteScalar()) != 2) throw new InvalidDataException("复制的数据库版本不匹配");
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, false);
                // Writer reopens at the new path after this control command. The source is retained intact.
                Volatile.Write(ref _directory, targetDirectory);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, token).ConfigureAwait(false);
    }
    public Task UseExistingDirectoryAsync(string directory) => MaintainAsync(connection =>
    {
        RequireStopped(); var target = Path.Combine(Path.GetFullPath(directory), "history.db");
        using var check = Open(true, target); using var verify = Command(check, "PRAGMA quick_check;");
        if (!Equals(verify.ExecuteScalar(), "ok")) throw new IOException("原存档备份校验失败");
        Volatile.Write(ref _directory, Path.GetFullPath(directory));
    }, CancellationToken.None);
}
