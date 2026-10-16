using System.Diagnostics;
using System.Text.Json;
using BilibiliLiveAssistant.Core;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BilibiliLiveAssistant.Infrastructure;

public sealed partial class SqliteLocalHistory : ILocalHistory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static long Millis(DateTimeOffset value) => value.ToUnixTimeMilliseconds();
    private static DateTimeOffset Time(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
    private static string Day(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    private SqliteConnection Open(bool readOnly = false, string? path = null)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path ?? DatabasePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 2 }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=2000;");
        return connection;
    }
    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string, object?)[] parameters)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static void Execute(SqliteConnection connection, string sql, params (string, object?)[] parameters)
    { using var command = Command(connection, sql, parameters); command.ExecuteNonQuery(); }
    // SQL transactions keep every command in the same native transaction, including migration batches.
    private sealed class HistoryTransaction : IDisposable
    {
        private readonly SqliteConnection _connection; private bool _committed;
        public HistoryTransaction(SqliteConnection connection) { _connection = connection; Execute(connection, "BEGIN IMMEDIATE;"); }
        public void Commit() { Execute(_connection, "COMMIT;"); _committed = true; }
        public void Dispose() { if (!_committed) { try { Execute(_connection, "ROLLBACK;"); } catch (SqliteException) { } } }
    }
    private void Initialize(SqliteConnection connection, bool recover)
    {
        Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        using var versionCommand = Command(connection, "PRAGMA user_version;");
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > 3) throw new InvalidDataException("历史数据库来自更新的软件版本，请使用新版打开");
        if (version == 0)
        {
            using var transaction = new HistoryTransaction(connection);
            Execute(connection, """
                CREATE TABLE anchors(uid INTEGER PRIMARY KEY CHECK(uid>0),name TEXT NOT NULL,avatar TEXT NOT NULL);
                CREATE TABLE room_aliases(room INTEGER NOT NULL,uid INTEGER NOT NULL REFERENCES anchors(uid),PRIMARY KEY(room,uid));
                CREATE INDEX ix_rooms_uid ON room_aliases(uid,room);
                CREATE TABLE anchor_names(uid INTEGER NOT NULL REFERENCES anchors(uid),name TEXT NOT NULL,first_seen INTEGER NOT NULL,PRIMARY KEY(uid,name));
                CREATE TABLE sessions(id TEXT PRIMARY KEY,room INTEGER NOT NULL,anchor_uid INTEGER REFERENCES anchors(uid),started INTEGER NOT NULL,
                    ended INTEGER,status TEXT NOT NULL,first_day TEXT NOT NULL,last_day TEXT NOT NULL,heartbeat INTEGER NOT NULL);
                CREATE INDEX ix_sessions_anchor ON sessions(anchor_uid,started);
                CREATE INDEX ix_sessions_room ON sessions(room,started);
                CREATE TABLE events(id INTEGER PRIMARY KEY AUTOINCREMENT,session_id TEXT NOT NULL REFERENCES sessions(id),received INTEGER NOT NULL,
                    upstream INTEGER,local_day TEXT NOT NULL,type INTEGER NOT NULL,uid INTEGER NOT NULL,name TEXT NOT NULL,content TEXT NOT NULL,
                    color TEXT NOT NULL,appearance TEXT,fields TEXT NOT NULL,source_id TEXT,quantity INTEGER,paid INTEGER,amount INTEGER);
                CREATE UNIQUE INDEX ix_events_source ON events(type,source_id) WHERE source_id IS NOT NULL;
                CREATE INDEX ix_events_time ON events(session_id,local_day,received DESC,id DESC);
                CREATE INDEX ix_events_type ON events(session_id,type,local_day,received DESC,id DESC);
                CREATE INDEX ix_events_uid ON events(session_id,uid,local_day,received DESC,id DESC);
                CREATE TABLE profiles(id INTEGER PRIMARY KEY,session_id TEXT NOT NULL REFERENCES sessions(id),received INTEGER NOT NULL,local_day TEXT NOT NULL,data TEXT NOT NULL);
                CREATE INDEX ix_profiles_time ON profiles(session_id,local_day);
                CREATE TABLE snapshots(id INTEGER PRIMARY KEY,session_id TEXT NOT NULL REFERENCES sessions(id),received INTEGER NOT NULL,local_day TEXT NOT NULL,
                    popularity INTEGER,watched INTEGER,rank TEXT,rank_total INTEGER);
                CREATE INDEX ix_snapshots_time ON snapshots(session_id,local_day);
                CREATE TABLE summaries(session_id TEXT NOT NULL REFERENCES sessions(id),local_day TEXT NOT NULL,type INTEGER NOT NULL,
                    count INTEGER NOT NULL,paid_amount INTEGER NOT NULL,free_quantity INTEGER NOT NULL,sc_amount INTEGER NOT NULL,unknown INTEGER NOT NULL,
                    PRIMARY KEY(session_id,local_day,type));
                CREATE TABLE gaps(id TEXT PRIMARY KEY,session_id TEXT REFERENCES sessions(id),room INTEGER NOT NULL,type INTEGER NOT NULL,local_day TEXT NOT NULL,last_day TEXT NOT NULL,
                    count INTEGER NOT NULL,first_at INTEGER NOT NULL,last_at INTEGER NOT NULL,reason TEXT NOT NULL);
                CREATE INDEX ix_gaps_time ON gaps(session_id,local_day);
                PRAGMA user_version=1;
                """);
            transaction.Commit();
        }
        if (version < 2)
        {
            using var transaction = new HistoryTransaction(connection);
            Execute(connection, """
                CREATE TABLE analysis_samples(id INTEGER PRIMARY KEY,session_id TEXT NOT NULL REFERENCES sessions(id),received INTEGER NOT NULL,
                    local_day TEXT NOT NULL,kind INTEGER NOT NULL,data TEXT NOT NULL);
                CREATE INDEX ix_analysis_time ON analysis_samples(session_id,local_day,received DESC,id DESC);
                CREATE TABLE member_changes(id INTEGER PRIMARY KEY,session_id TEXT NOT NULL REFERENCES sessions(id),received INTEGER NOT NULL,
                    local_day TEXT NOT NULL,source TEXT NOT NULL,uid INTEGER NOT NULL,kind TEXT NOT NULL,before_data TEXT,after_data TEXT NOT NULL);
                CREATE INDEX ix_changes_time ON member_changes(session_id,local_day,received DESC,id DESC);
                CREATE INDEX ix_changes_member ON member_changes(session_id,source,uid,received DESC,id DESC);
                CREATE INDEX ix_profiles_received ON profiles(session_id,received DESC);
                PRAGMA user_version=2;
                """);
            transaction.Commit();
        }
        if (version < 3)
        {
            using var transaction = new HistoryTransaction(connection);
            Execute(connection, """
                CREATE TABLE broadcasts(id TEXT PRIMARY KEY,anchor_uid INTEGER NOT NULL REFERENCES anchors(uid),room INTEGER NOT NULL,
                    started INTEGER NOT NULL,title TEXT NOT NULL,first_seen INTEGER NOT NULL,last_seen INTEGER NOT NULL,ended_observed INTEGER,
                    UNIQUE(anchor_uid,room,started));
                CREATE INDEX ix_broadcast_anchor ON broadcasts(anchor_uid,started DESC);
                CREATE TABLE broadcast_spans(session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                    started INTEGER NOT NULL,ended INTEGER,broadcast_id TEXT NOT NULL REFERENCES broadcasts(id),
                    PRIMARY KEY(session_id,started));
                CREATE INDEX ix_broadcast_spans ON broadcast_spans(broadcast_id,session_id,started,ended);
                PRAGMA user_version=3;
                """);
            transaction.Commit();
        }
        if (recover)
            Execute(connection, "UPDATE sessions SET status='interrupted',ended=heartbeat WHERE status='active';");
    }
    private static void EnsureSession(SqliteConnection connection, HistoryContext context)
    {
        Execute(connection, "INSERT OR IGNORE INTO sessions(id,room,started,status,first_day,last_day,heartbeat) VALUES($id,$room,$start,'active',$day,$day,$start)",
            ("$id", context.Id), ("$room", context.RoomId), ("$start", Millis(context.StartedAt)), ("$day", Day(context.StartedAt)));
    }
    private void WriteEnvelope(SqliteConnection connection, HistoryEnvelope item)
    {
        EnsureSession(connection, item.Context);
        Execute(connection, "UPDATE sessions SET first_day=MIN(first_day,$day),last_day=MAX(last_day,$day),heartbeat=MAX(heartbeat,$at) WHERE id=$id", ("$day", item.LocalDay), ("$at", Millis(item.ReceivedAt)), ("$id", item.Context.Id));
        var value = item.Event; var fields = item.Fields;
        if (value.Type is LiveEventType.Online or LiveEventType.Rank)
        {
            WriteSnapshot(connection, item); return;
        }
        var paid = fields.CoinType switch { "gold" => (int?)1, "silver" => 0, _ => null };
        var appearance = value.Appearance is null ? null : JsonSerializer.Serialize(value.Appearance, Json);
        using var command = Command(connection, """
            INSERT OR IGNORE INTO events(session_id,received,upstream,local_day,type,uid,name,content,color,appearance,fields,source_id,quantity,paid,amount)
            VALUES($s,$at,$up,$day,$type,$uid,$name,$content,$color,$appearance,$fields,$source,$quantity,$paid,$amount);
            """, ("$s", item.Context.Id), ("$at", Millis(item.ReceivedAt)), ("$up", fields.UpstreamTime is { } up ? Millis(up) : null),
            ("$day", item.LocalDay), ("$type", (int)value.Type), ("$uid", Math.Max(0, value.Uid)), ("$name", value.Username), ("$content", value.Content),
            ("$color", value.Color), ("$appearance", appearance), ("$fields", JsonSerializer.Serialize(fields, Json)), ("$source", fields.UniqueId),
            ("$quantity", fields.Quantity), ("$paid", paid), ("$amount", fields.AmountMilliYuan));
        if (command.ExecuteNonQuery() == 0) return;
        WriteObservedMember(connection, item);
        var isGift = value.Type is LiveEventType.Gift or LiveEventType.Guard;
        var unknown = (isGift && paid != 0 || value.Type == LiveEventType.SuperChat) && fields.AmountMilliYuan is null ? 1 : 0;
        Execute(connection, """
            INSERT INTO summaries VALUES($s,$day,$type,1,$paid,$free,$sc,$unknown)
            ON CONFLICT(session_id,local_day,type) DO UPDATE SET count=count+1,paid_amount=paid_amount+excluded.paid_amount,
            free_quantity=free_quantity+excluded.free_quantity,sc_amount=sc_amount+excluded.sc_amount,unknown=unknown+excluded.unknown;
            """, ("$s", item.Context.Id), ("$day", item.LocalDay), ("$type", (int)value.Type),
            ("$paid", isGift && paid == 1 ? fields.AmountMilliYuan ?? 0 : 0), ("$free", isGift && paid == 0 ? fields.Quantity ?? 0 : 0),
            ("$sc", value.Type == LiveEventType.SuperChat ? fields.AmountMilliYuan ?? 0 : 0), ("$unknown", unknown));
    }
    private void WriteSnapshot(SqliteConnection connection, HistoryEnvelope item)
    {
        // Snapshot payloads contain only public, typed room data, never raw metadata.
        var value = item.Event;
        if (value.Extra.GetValueOrDefault("archive_analysis") is HistoryAnalysisSample analysis)
        { WriteAnalysis(connection, item, analysis); return; }
        if (value.Extra.TryGetValue("room_profile", out var profileValue) && profileValue is LiveRoomProfile profile)
        {
            var data = JsonSerializer.Serialize(profile, Json);
            using var lastCommand = Command(connection, "SELECT data FROM profiles WHERE session_id=$s ORDER BY id DESC LIMIT 1", ("$s", item.Context.Id));
            if (!Equals(lastCommand.ExecuteScalar(), data))
                Execute(connection, "INSERT INTO profiles(session_id,received,local_day,data) VALUES($s,$at,$day,$data)",
                    ("$s", item.Context.Id), ("$at", Millis(item.ReceivedAt)), ("$day", item.LocalDay), ("$data", data));
            if (profile.Uid > 0)
            {
                Execute(connection, "INSERT INTO anchors VALUES($uid,$name,$avatar) ON CONFLICT(uid) DO UPDATE SET name=excluded.name,avatar=excluded.avatar",
                    ("$uid", profile.Uid), ("$name", profile.Name), ("$avatar", profile.AvatarUrl));
                Execute(connection, "INSERT OR IGNORE INTO anchor_names VALUES($uid,$name,$at)", ("$uid", profile.Uid), ("$name", profile.Name), ("$at", Millis(item.ReceivedAt)));
                foreach (var room in new[] { item.Context.RoomId, profile.RoomId }.Distinct().Where(r => r > 0))
                {
                    Execute(connection, "INSERT OR IGNORE INTO room_aliases VALUES($room,$uid)", ("$room", room), ("$uid", profile.Uid));
                    Execute(connection, "UPDATE sessions SET anchor_uid=$uid WHERE anchor_uid IS NULL AND room=$room", ("$uid", profile.Uid), ("$room", room));
                }
                Execute(connection, "UPDATE sessions SET anchor_uid=$uid WHERE id=$s", ("$uid", profile.Uid), ("$s", item.Context.Id));
            }
            WriteBroadcastProfile(connection, item, profile);
        }
        if (value.Extra.GetValueOrDefault("archive_profile_only") is true) return;
        // Capture merges the most recently observed fields before throttling to 60 seconds.
        Execute(connection, "INSERT INTO snapshots(session_id,received,local_day,popularity,watched,rank,rank_total) VALUES($s,$at,$day,$p,$w,$rank,$total)",
            ("$s", item.Context.Id), ("$at", Millis(item.ReceivedAt)), ("$day", item.LocalDay),
            ("$p", value.Extra.GetValueOrDefault("archive_popularity")), ("$w", value.Extra.GetValueOrDefault("archive_watched")),
            ("$rank", value.Extra.GetValueOrDefault("rank_members") is IReadOnlyList<LiveRankMember> members ? JsonSerializer.Serialize(members, Json) : null),
            ("$total", value.Extra.GetValueOrDefault("rank_total")));
    }
    private static void WriteGap(SqliteConnection connection, HistoryGap gap)
    {
        Execute(connection, """
            INSERT INTO gaps(id,session_id,room,type,local_day,last_day,count,first_at,last_at,reason) VALUES($id,$s,$room,$type,$day,$lastDay,$count,$first,$last,$why)
            ON CONFLICT(id) DO UPDATE SET count=MAX(count,excluded.count),first_at=MIN(first_at,excluded.first_at),last_at=MAX(last_at,excluded.last_at),last_day=MAX(last_day,excluded.last_day)
            """, ("$id", gap.Id),
            ("$s", gap.SessionId), ("$room", gap.RoomId), ("$type", (int)gap.Type), ("$day", Day(gap.FirstAt)), ("$lastDay", Day(gap.LastAt)), ("$count", gap.Count),
            ("$first", Millis(gap.FirstAt)), ("$last", Millis(gap.LastAt)), ("$why", gap.Reason));
    }
    private async Task<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        await _readGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                timeout.Token.ThrowIfCancellationRequested();
                using var connection = Open(true);
                raw.sqlite3_progress_handler(connection.Handle, 1000, _ => timeout.IsCancellationRequested ? 1 : 0, null);
                try { var result = read(connection); timeout.Token.ThrowIfCancellationRequested(); return result; }
                catch (SqliteException) when (timeout.IsCancellationRequested) { throw new OperationCanceledException(timeout.Token); }
                finally { raw.sqlite3_progress_handler(connection.Handle, 0, null, null); }
            }, timeout.Token).ConfigureAwait(false);
        }
        finally { _readGate.Release(); }
    }
    private static string Scope(HistoryFilter filter, SqliteCommand command, string table = "e", string timeColumn = "received")
    {
        command.Parameters.AddWithValue("$from", filter.FromDay); command.Parameters.AddWithValue("$to", filter.ToDay);
        var where = $"{table}.local_day >= $from AND {table}.local_day <= $to";
        if (filter.Anchor.Uid is > 0)
        { where += " AND s.anchor_uid=$anchor"; command.Parameters.AddWithValue("$anchor", filter.Anchor.Uid); }
        else
        { where += " AND s.anchor_uid IS NULL AND s.room=$room"; command.Parameters.AddWithValue("$room", filter.Anchor.RoomId); }
        if (filter.SessionId is { Length: > 0 }) { where += " AND s.id=$session"; command.Parameters.AddWithValue("$session", filter.SessionId); }
        if (filter.BroadcastId is { Length: > 0 } broadcast)
        {
            var span = table == "s" ? "p.session_id=s.id" : timeColumn == "first_at"
                ? $"p.session_id=s.id AND p.started<={table}.last_at AND (p.ended IS NULL OR p.ended>{table}.first_at)"
                : $"p.session_id=s.id AND p.started<={table}.{timeColumn} AND (p.ended IS NULL OR p.ended>{table}.{timeColumn})";
            if (broadcast == HistoryBroadcast.Unidentified)
            {
                // A session may contain both identified and unidentified periods.
                where += table == "s"
                    ? " AND (NOT EXISTS(SELECT 1 FROM broadcast_spans p WHERE p.session_id=s.id AND p.started<=s.started AND (p.ended IS NULL OR p.ended>s.started)) OR EXISTS(SELECT 1 FROM broadcast_spans p WHERE p.session_id=s.id AND p.ended<COALESCE(s.ended,s.heartbeat) AND NOT EXISTS(SELECT 1 FROM broadcast_spans q WHERE q.session_id=s.id AND q.started<=p.ended AND (q.ended IS NULL OR q.ended>p.ended))))"
                    : $" AND NOT EXISTS(SELECT 1 FROM broadcast_spans p WHERE {span})";
            }
            else
            { where += $" AND EXISTS(SELECT 1 FROM broadcast_spans p WHERE {span} AND p.broadcast_id=$broadcast)"; command.Parameters.AddWithValue("$broadcast", broadcast); }
        }
        return where;
    }
    public Task<IReadOnlyList<HistoryAnchor>> AnchorsAsync(CancellationToken token, string search = "", int limit = 2000, int offset = 0) => ReadAsync<IReadOnlyList<HistoryAnchor>>(connection =>
    {
        using var command = Command(connection, """
            SELECT s.anchor_uid,MIN(s.room),COALESCE(a.name,''),COALESCE(a.avatar,'') FROM sessions s LEFT JOIN anchors a ON a.uid=s.anchor_uid
            WHERE $search='' OR CAST(a.uid AS TEXT)=$search OR CAST(s.room AS TEXT)=$search OR instr(COALESCE(a.name,''),$search)>0
                OR EXISTS(SELECT 1 FROM anchor_names n WHERE n.uid=a.uid AND instr(n.name,$search)>0)
                OR EXISTS(SELECT 1 FROM room_aliases r WHERE r.uid=a.uid AND CAST(r.room AS TEXT)=$search)
            GROUP BY COALESCE('u:'||s.anchor_uid,'r:'||s.room) ORDER BY MAX(s.started) DESC,COALESCE('u:'||s.anchor_uid,'r:'||s.room) LIMIT $limit OFFSET $offset;
            """, ("$search", search), ("$limit", Math.Clamp(limit, 1, 2000)), ("$offset", Math.Max(0, offset)));
        using var reader = command.ExecuteReader(); var rows = new List<HistoryAnchor>();
        while (reader.Read()) rows.Add(new(reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }, token);
    public Task<IReadOnlyList<HistorySession>> SessionsAsync(HistoryFilter filter, CancellationToken token) => ReadAsync<IReadOnlyList<HistorySession>>(connection =>
    {
        using var command = connection.CreateCommand();
        var where = Scope(filter with { SessionId = null }, command, "s").Replace("s.local_day >= $from AND s.local_day <= $to", "s.last_day >= $from AND s.first_day <= $to");
        command.CommandText = $"SELECT id,room,started,ended,status,first_day,last_day FROM sessions s WHERE {where} ORDER BY started DESC LIMIT 500";
        using var reader = command.ExecuteReader(); var rows = new List<HistorySession>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetInt32(1), Time(reader.GetInt64(2)), reader.IsDBNull(3) ? null : Time(reader.GetInt64(3)), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return rows;
    }, token);
    public Task<IReadOnlyList<HistoryRow>> QueryAsync(HistoryFilter filter, CancellationToken token) => ReadAsync<IReadOnlyList<HistoryRow>>(connection =>
    {
        using var command = connection.CreateCommand(); var where = MessageWhere(filter, command);
        var sort = filter.Trade?.Normalize().Sort ?? MessageSort.Newest;
        var oldest = sort == MessageSort.Oldest;
        var metric = sort is MessageSort.AmountAscending or MessageSort.AmountDescending ? "e.amount" : sort == MessageSort.QuantityDescending ? "e.quantity" : null;
        var ascending = sort == MessageSort.AmountAscending;
        var timeOrder = oldest ? "ASC" : "DESC";
        var order = metric is null ? $"e.received {timeOrder},e.id {timeOrder}" : $"({metric} IS NULL) ASC,{metric} {(ascending ? "ASC" : "DESC")},e.received DESC,e.id DESC";
        if (filter.BeforeId is { } id && filter.BeforeReceivedAt is { } before)
        {
            var op = oldest ? ">" : "<";
            var tie = $"(e.received{op}$beforeAt OR e.received=$beforeAt AND e.id{op}$before)";
            if (metric is null) where += " AND " + tie;
            else if (filter.BeforeSortValue is { } value)
            { where += $" AND ({metric} IS NULL OR {metric}{(ascending ? ">" : "<")}$sortValue OR {metric}=$sortValue AND {tie})"; command.Parameters.AddWithValue("$sortValue", value); }
            else where += $" AND {metric} IS NULL AND {tie}";
            command.Parameters.AddWithValue("$before", id); command.Parameters.AddWithValue("$beforeAt", Millis(before));
        }
        command.CommandText = $"SELECT e.id,e.received,e.upstream,e.local_day,e.session_id,e.type,e.uid,e.name,e.content,e.color,e.appearance,e.fields FROM events e JOIN sessions s ON s.id=e.session_id WHERE {where} ORDER BY {order} LIMIT 100";
        using var reader = command.ExecuteReader(); var rows = new List<HistoryRow>(100);
        while (reader.Read())
        {
            var at = Time(reader.GetInt64(1)); var fields = JsonSerializer.Deserialize<HistoryEventFields>(reader.GetString(11), Json) ?? new();
            var appearance = reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<LiveMessageAppearance>(reader.GetString(10), Json);
            var value = new LiveEvent((LiveEventType)reader.GetInt32(5), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetInt64(6), at.ToLocalTime(), Appearance: appearance) { HistoryFields = fields };
            rows.Add(new(reader.GetInt64(0), at, reader.IsDBNull(2) ? null : Time(reader.GetInt64(2)), reader.GetString(3), reader.GetString(4), value, fields));
        }
        return rows;
    }, token);
    public Task<long> MatchingCountAsync(HistoryFilter filter, CancellationToken token) => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand(); var where = MessageWhere(filter, command);
        command.CommandText = $"SELECT COUNT(*) FROM events e JOIN sessions s ON s.id=e.session_id WHERE {where}";
        return Convert.ToInt64(command.ExecuteScalar());
    }, token);
    private static string MessageWhere(HistoryFilter filter, SqliteCommand command)
    {
        var where = Scope(filter, command);
        if (filter.Type is { } type)
        {
            where += type == LiveEventType.Gift && filter.IncludeGuardGifts ? " AND e.type IN (2,4)" : " AND e.type=$type";
            if (type != LiveEventType.Gift || !filter.IncludeGuardGifts) command.Parameters.AddWithValue("$type", (int)type);
        }
        if (filter.SenderUid is { } uid) { where += " AND e.uid=$uid"; command.Parameters.AddWithValue("$uid", uid); }
        if (filter.Nickname.Length > 0) { where += " AND instr(e.name,$name)>0"; command.Parameters.AddWithValue("$name", filter.Nickname); }
        if (filter.Keyword.Length > 0) { where += " AND instr(e.content,$keyword)>0"; command.Parameters.AddWithValue("$keyword", filter.Keyword); }
        var trade = (filter.Trade ?? new()).Normalize();
        if (filter.Type is LiveEventType.Gift or LiveEventType.Guard)
        {
            where += trade.Category switch { GiftCategory.Paid => " AND e.type=2 AND e.paid=1", GiftCategory.Free => " AND e.type=2 AND e.paid=0", GiftCategory.Guard => " AND e.type=4", _ => "" };
            if (trade.GiftName.Length > 0) { where += " AND json_extract(e.fields,'$.giftName')=$giftName COLLATE BINARY"; command.Parameters.AddWithValue("$giftName", trade.GiftName); }
        }
        if (filter.Type == LiveEventType.SuperChat)
        {
            if (trade.MinimumMilliYuan is { } min) { where += " AND e.amount >= $minAmount"; command.Parameters.AddWithValue("$minAmount", min); }
            if (trade.MaximumMilliYuan is { } max) { where += " AND e.amount <= $maxAmount"; command.Parameters.AddWithValue("$maxAmount", max); }
        }
        return where;
    }
    public Task<HistorySummary> SummaryAsync(HistoryFilter filter, CancellationToken token) => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand(); var where = Scope(filter, command);
        command.CommandText = filter.BroadcastId is null ? $"""
            SELECT COALESCE(SUM(count),0),COALESCE(SUM(CASE WHEN type=0 THEN count ELSE 0 END),0),
            COALESCE(SUM(CASE WHEN type=1 THEN count ELSE 0 END),0),COALESCE(SUM(CASE WHEN type=2 THEN count ELSE 0 END),0),
            COALESCE(SUM(CASE WHEN type=4 THEN count ELSE 0 END),0),COALESCE(SUM(CASE WHEN type=3 THEN count ELSE 0 END),0),
            COALESCE(SUM(paid_amount),0),COALESCE(SUM(free_quantity),0),COALESCE(SUM(sc_amount),0),COALESCE(SUM(unknown),0)
            FROM summaries e JOIN sessions s ON s.id=e.session_id WHERE {where}
            """ : $"""
            SELECT COUNT(*),COALESCE(SUM(type=0),0),COALESCE(SUM(type=1),0),COALESCE(SUM(type=2),0),COALESCE(SUM(type=4),0),COALESCE(SUM(type=3),0),
            COALESCE(SUM(CASE WHEN type IN(2,4) AND paid=1 THEN amount ELSE 0 END),0),
            COALESCE(SUM(CASE WHEN type IN(2,4) AND paid=0 THEN quantity ELSE 0 END),0),
            COALESCE(SUM(CASE WHEN type=3 THEN amount ELSE 0 END),0),
            COALESCE(SUM(CASE WHEN (type IN(2,4) AND (paid IS NULL OR paid<>0) OR type=3) AND amount IS NULL THEN 1 ELSE 0 END),0)
            FROM events e JOIN sessions s ON s.id=e.session_id WHERE {where}
            """;
        long[] totals;
        using (var reader = command.ExecuteReader()) { reader.Read(); totals = Enumerable.Range(0, 10).Select(reader.GetInt64).ToArray(); }
        using var gapCommand = connection.CreateCommand(); var gapWhere = Scope(filter, gapCommand, timeColumn: "first_at");
        gapCommand.CommandText = $"SELECT COALESCE(SUM(count),0) FROM gaps e JOIN sessions s ON s.id=e.session_id WHERE {gapWhere}";
        var missing = Convert.ToInt64(gapCommand.ExecuteScalar());
        command.CommandText = "SELECT COALESCE(SUM(count),0) FROM gaps WHERE session_id IS NULL AND local_day<=$to AND last_day>=$from";
        var unassigned = Convert.ToInt64(command.ExecuteScalar());
        using var interruptedCommand = connection.CreateCommand();
        var sessionScope = Scope(filter, interruptedCommand, "s").Replace("s.local_day >= $from AND s.local_day <= $to", "s.last_day >= $from AND s.first_day <= $to");
        interruptedCommand.CommandText = $"SELECT COUNT(*) FROM sessions s WHERE {sessionScope} AND status='interrupted'";
        var interrupted = Convert.ToInt64(interruptedCommand.ExecuteScalar());
        return new HistorySummary(totals[0], totals[1], totals[2], totals[3], totals[4], totals[5], totals[6], totals[7], totals[8], totals[9], missing, missing > 0 || unassigned > 0 || interrupted > 0, unassigned, interrupted);
    }, token);
    public Task<IReadOnlyList<HistoryGap>> GapsAsync(HistoryFilter filter, CancellationToken token) => ReadAsync<IReadOnlyList<HistoryGap>>(connection =>
    {
        using var command = connection.CreateCommand(); var where = Scope(filter, command, timeColumn: "first_at");
        command.CommandText = $"SELECT e.session_id,e.room,e.type,e.count,e.first_at,e.last_at,e.reason FROM gaps e LEFT JOIN sessions s ON s.id=e.session_id WHERE ({where}) OR (e.session_id IS NULL AND e.local_day<=$to AND e.last_day>=$from) ORDER BY first_at DESC LIMIT 100";
        using var reader = command.ExecuteReader(); var result = new List<HistoryGap>();
        while (reader.Read()) result.Add(new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt32(1), (LiveEventType)reader.GetInt32(2), reader.GetInt64(3), Time(reader.GetInt64(4)), Time(reader.GetInt64(5)), reader.GetString(6)));
        return result;
    }, token);
    public Task<long> CountAsync(HistoryFilter filter, CancellationToken token) => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand(); var where = Scope(filter, command);
        var gapWhere = where.Replace("p.started<=e.received", "p.started<=e.last_at").Replace("p.ended>e.received", "p.ended>e.first_at");
        if (filter.BroadcastId is { } broadcast && broadcast != HistoryBroadcast.Unidentified)
            gapWhere += " AND EXISTS(SELECT 1 FROM broadcast_spans p WHERE p.session_id=s.id AND p.broadcast_id=$broadcast AND p.started<=e.first_at AND (p.ended IS NULL OR p.ended>e.last_at))";
        command.CommandText = "SELECT SUM(n) FROM (" + string.Join(" UNION ALL ", new[] { "events", "profiles", "snapshots", "analysis_samples", "member_changes", "gaps" }
            .Select(table => $"SELECT COUNT(*) AS n FROM {table} e JOIN sessions s ON s.id=e.session_id WHERE {(table == "gaps" ? gapWhere : where)}")) + ")";
        return Convert.ToInt64(command.ExecuteScalar());
    }, token);
    public Task<HistoryDetails> DetailsAsync(HistoryFilter filter, CancellationToken token) => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand(); var where = Scope(filter, command);
        command.CommandText = $"SELECT data FROM profiles e JOIN sessions s ON s.id=e.session_id WHERE {where} ORDER BY received DESC LIMIT 1";
        var profile = command.ExecuteScalar() is string data ? JsonSerializer.Deserialize<LiveRoomProfile>(data, Json) : null;
        command.CommandText = $"SELECT COUNT(*) FROM snapshots e JOIN sessions s ON s.id=e.session_id WHERE {where}";
        var count = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = $"SELECT received,popularity,watched,rank,rank_total FROM snapshots e JOIN sessions s ON s.id=e.session_id WHERE {where} ORDER BY received DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new HistoryDetails(profile, count, null, null, null, null, null);
        return new HistoryDetails(profile, count, Time(reader.GetInt64(0)), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<LiveRankMember[]>(reader.GetString(3), Json), reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }, token);
}
