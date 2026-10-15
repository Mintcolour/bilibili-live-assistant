using System.Globalization;
using BilibiliLiveAssistant.Core;
using Microsoft.Data.Sqlite;

namespace BilibiliLiveAssistant.Infrastructure;

public sealed partial class SqliteLocalHistory
{
    // Associations are time spans, not a single session foreign key: one long
    // monitoring session can cross a real restart, and several sessions can
    // observe the same broadcast. Unobserved periods never become saved data.
    private static void WriteBroadcastProfile(SqliteConnection c, HistoryEnvelope item, LiveRoomProfile profile)
    {
        var at = Millis(item.ReceivedAt);
        using (var newest = Command(c, "SELECT MAX(received) FROM profiles WHERE session_id=$s", ("$s", item.Context.Id)))
            if (newest.ExecuteScalar() is long latest && latest > at) return;
        string? previousId = null; long? previousStart = null, previousEnd = null;
        using (var command = Command(c, "SELECT broadcast_id,started,ended FROM broadcast_spans WHERE session_id=$s ORDER BY started DESC LIMIT 1", ("$s", item.Context.Id)))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) { previousId = reader.GetString(0); previousStart = reader.GetInt64(1); previousEnd = reader.IsDBNull(2) ? null : reader.GetInt64(2); }
        if (previousStart > at) return;
        if (profile.LiveStatus is null) return; // Older/missing profiles do not prove a stop.
        var reliable = profile.LiveStatus == 1 && profile.Uid > 0 && profile.RoomId > 0
            && profile.LiveStartedAt is { } liveStart && liveStart <= item.ReceivedAt;
        if (!reliable)
        {
            if (previousId is not null && previousEnd is null)
            {
                Execute(c, "UPDATE broadcast_spans SET ended=$at WHERE session_id=$s AND ended IS NULL", ("$at", at), ("$s", item.Context.Id));
                if (profile.LiveStatus is 0 or 2)
                    Execute(c, "UPDATE broadcasts SET ended_observed=COALESCE(ended_observed,$at) WHERE id=$id", ("$at", at), ("$id", previousId));
            }
            return;
        }
        var started = Millis(profile.LiveStartedAt!.Value);
        var id = FormattableString.Invariant($"{profile.Uid}:{profile.RoomId}:{started}");
        Execute(c, """
            INSERT INTO broadcasts(id,anchor_uid,room,started,title,first_seen,last_seen)
            VALUES($id,$uid,$room,$start,$title,$at,$at)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title,first_seen=MIN(first_seen,excluded.first_seen),last_seen=MAX(last_seen,excluded.last_seen);
            """, ("$id", id), ("$uid", profile.Uid), ("$room", profile.RoomId), ("$start", started), ("$title", profile.Title), ("$at", at));
        if (previousId == id && previousEnd is null) return;
        var boundary = Math.Max(Millis(item.Context.StartedAt), started);
        // Re-observing the same start after an explicit gap resumes at this
        // observation, rather than assigning the unverified gap retrospectively.
        if (previousEnd is { } ended) boundary = Math.Max(boundary, Math.Max(ended, previousId == id ? at : ended));
        if (previousStart is { } lastStart) boundary = Math.Max(boundary, lastStart);
        if (previousId is not null && previousEnd is null)
            Execute(c, "UPDATE broadcast_spans SET ended=$at WHERE session_id=$s AND ended IS NULL", ("$at", boundary), ("$s", item.Context.Id));
        Execute(c, """
            INSERT INTO broadcast_spans(session_id,started,ended,broadcast_id) VALUES($s,$at,NULL,$id)
            ON CONFLICT(session_id,started) DO UPDATE SET ended=NULL,broadcast_id=excluded.broadcast_id;
            """, ("$s", item.Context.Id), ("$at", boundary), ("$id", id));
    }

    public Task<IReadOnlyList<HistoryBroadcast>> BroadcastsAsync(HistoryFilter filter, CancellationToken token) => ReadAsync<IReadOnlyList<HistoryBroadcast>>(c =>
    {
        filter = filter with { SessionId = null, BroadcastId = null };
        using var command = c.CreateCommand();
        var where = Scope(filter, command, "s").Replace("s.local_day >= $from AND s.local_day <= $to", "s.last_day >= $from AND s.first_day <= $to");
        static long Boundary(string day, bool end)
        {
            var date = DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (date == DateTime.MinValue.Date) return DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
            if (date == DateTime.MaxValue.Date) return DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
            if (end) date = date.AddDays(1);
            return new DateTimeOffset(date).ToUnixTimeMilliseconds();
        }
        command.Parameters.AddWithValue("$startAt", Boundary(filter.FromDay, false));
        command.Parameters.AddWithValue("$endAt", Boundary(filter.ToDay, true));
        command.CommandText = $"""
            SELECT b.id,b.room,b.started,b.title,COUNT(DISTINCT s.id),MIN(p.started),MAX(COALESCE(p.ended,s.ended,s.heartbeat))
            FROM broadcasts b JOIN broadcast_spans p ON p.broadcast_id=b.id JOIN sessions s ON s.id=p.session_id
            WHERE {where} AND p.started<$endAt AND COALESCE(p.ended,s.ended,s.heartbeat)>=$startAt
            GROUP BY b.id ORDER BY b.started DESC LIMIT 500;
            """;
        var result = new List<HistoryBroadcast>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt32(1), Time(reader.GetInt64(2)), reader.GetString(3), reader.GetInt32(4), Time(reader.GetInt64(5)), Time(reader.GetInt64(6))));
        using var unknown = c.CreateCommand();
        var unknownWhere = Scope(filter with { BroadcastId = HistoryBroadcast.Unidentified }, unknown, "s")
            .Replace("s.local_day >= $from AND s.local_day <= $to", "s.last_day >= $from AND s.first_day <= $to");
        unknown.CommandText = $"SELECT COUNT(*),MIN(started),MAX(COALESCE(ended,heartbeat)) FROM sessions s WHERE {unknownWhere}";
        using var unassigned = unknown.ExecuteReader();
        if (unassigned.Read() && unassigned.GetInt64(0) > 0)
            result.Add(new(HistoryBroadcast.Unidentified, filter.Anchor.RoomId, null, "未识别场次", unassigned.GetInt32(0), Time(unassigned.GetInt64(1)), Time(unassigned.GetInt64(2))));
        return result;
    }, token);
}
