using System.Text.Json;
using BilibiliLiveAssistant.Core;
using Microsoft.Data.Sqlite;

namespace BilibiliLiveAssistant.Infrastructure;

public sealed partial class SqliteLocalHistory
{
    private void WriteAnalysis(SqliteConnection c, HistoryEnvelope item, HistoryAnalysisSample sample)
    {
        sample = sample with { Collected = sample.Members.Count };
        Execute(c, "INSERT INTO analysis_samples(session_id,received,local_day,kind,data) VALUES($s,$at,$day,$kind,$data)",
            ("$s", item.Context.Id), ("$at", Millis(sample.At)), ("$day", Day(sample.At)), ("$kind", (int)sample.Kind), ("$data", JsonSerializer.Serialize(sample, Json)));
        var source = sample.Kind == HistorySampleKind.Guards ? "guards" : "energy";
        foreach (var member in sample.Members.Where(m => m.Uid > 0))
            WriteMember(c, item.Context.Id, sample.At, source, new(member.Uid, member.Username, CompactAppearance(member.Appearance), member.Rank, member.Contribution, member.AccompanyDays),
                "观察变化", member.HasMedalData, member.HasGuardData);
    }
    private static LiveMessageAppearance CompactAppearance(LiveMessageAppearance a) => new()
    { AvatarUrl = a.AvatarUrl, MedalName = a.MedalName, MedalLevel = a.MedalLevel, MedalColor = a.MedalColor, GuardLevel = a.GuardLevel };
    private void WriteObservedMember(SqliteConnection c, HistoryEnvelope item)
    {
        var e = item.Event; if (e.Uid <= 0) return;
        var a = CompactAppearance(e.Appearance ?? new());
        if (e.Type == LiveEventType.Guard)
            WriteMember(c, item.Context.Id, item.ReceivedAt, "guard_event", new(e.Uid, e.Username, a with { GuardLevel = item.Fields.GuardLevel ?? a.GuardLevel }), "上舰事件");
        // The old appearance model does not carry medal anchor UID. Store observations without claiming room membership.
        if (a.MedalLevel > 0 && a.MedalName.Length > 0)
            WriteMember(c, item.Context.Id, item.ReceivedAt, "medal_observed", new(e.Uid, e.Username, a), "粉丝牌观察");
    }
    private void WriteMember(SqliteConnection c, string session, DateTimeOffset at, string source, HistoryMemberState next, string kind,
        bool hasMedal = true, bool hasGuard = true)
    {
        using var last = Command(c, "SELECT after_data FROM member_changes WHERE session_id=$s AND source=$source AND uid=$uid ORDER BY received DESC,id DESC LIMIT 1",
            ("$s", session), ("$source", source), ("$uid", next.Uid));
        var previous = last.ExecuteScalar() as string;
        var before = previous is null ? null : JsonSerializer.Deserialize<HistoryMemberState>(previous, Json);
        if (before is not null)
        {
            var a = next.Appearance;
            if (!hasMedal) a = a with { MedalName = before.Appearance.MedalName, MedalLevel = before.Appearance.MedalLevel, MedalColor = before.Appearance.MedalColor };
            if (!hasGuard) a = a with { GuardLevel = before.Appearance.GuardLevel };
            next = next with { Appearance = a };
        }
        var data = JsonSerializer.Serialize(next, Json);
        if (data == previous && source != "guard_event") return;
        Execute(c, "INSERT INTO member_changes(session_id,received,local_day,source,uid,kind,before_data,after_data) VALUES($s,$at,$day,$source,$uid,$kind,$before,$after)",
            ("$s", session), ("$at", Millis(at)), ("$day", Day(at)), ("$source", source), ("$uid", next.Uid),
            ("$kind", before is null && source != "guard_event" ? "首次观察" : kind), ("$before", previous), ("$after", data));
    }
    public Task<IReadOnlyList<HistoryTrendPoint>> TrendsAsync(HistoryFilter filter, CancellationToken token) => ReadAsync<IReadOnlyList<HistoryTrendPoint>>(c =>
    {
        using var command = c.CreateCommand(); var where = Scope(filter, command);
        command.CommandText = $"SELECT e.id,e.session_id,json_set(e.data,'$.members',json('[]')) FROM analysis_samples e JOIN sessions s ON s.id=e.session_id WHERE {where} ORDER BY e.received DESC,e.id DESC LIMIT 1200";
        var result = new List<HistoryTrendPoint>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) if (JsonSerializer.Deserialize<HistoryAnalysisSample>(reader.GetString(2), Json) is { } sample)
                result.Add(new(reader.GetInt64(0), reader.GetString(1), sample));
        if (result.Count == 0)
        {
            command.CommandText = $"""
                SELECT e.id,e.session_id,e.received,e.popularity,e.watched,e.rank_total,
                (SELECT data FROM profiles p WHERE p.session_id=e.session_id AND p.received<=e.received ORDER BY p.received DESC LIMIT 1)
                FROM snapshots e JOIN sessions s ON s.id=e.session_id WHERE {where} ORDER BY e.received DESC,e.id DESC LIMIT 1200
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var p = reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<LiveRoomProfile>(reader.GetString(6), Json);
                result.Add(new(reader.GetInt64(0), reader.GetString(1), new(HistorySampleKind.Counts, Time(reader.GetInt64(2)), 60, p?.FansClub, p?.Guards,
                    reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4), [], 0, null, "旧版缓存采样，新鲜度未知")));
            }
        }
        return result.OrderBy(p => p.Sample.At).ToArray();
    }, token);
    public Task<HistoryAnalysisSample?> SampleAsync(HistoryFilter filter, long id, CancellationToken token) => ReadAsync(c =>
    {
        using var command = c.CreateCommand(); var where = Scope(filter, command);
        command.Parameters.AddWithValue("$id", id);
        command.CommandText = $"SELECT e.data FROM analysis_samples e JOIN sessions s ON s.id=e.session_id WHERE {where} AND e.id=$id LIMIT 1";
        return command.ExecuteScalar() is string data ? JsonSerializer.Deserialize<HistoryAnalysisSample>(data, Json) : null;
    }, token);
    public Task<IReadOnlyList<HistoryMemberChange>> ChangesAsync(HistoryFilter filter, HistoryChangeCursor? cursor, CancellationToken token) => ReadAsync<IReadOnlyList<HistoryMemberChange>>(c =>
    {
        using var command = c.CreateCommand(); var where = Scope(filter, command);
        if (cursor is not null) { where += " AND (e.received<$at OR e.received=$at AND e.id<$id)"; command.Parameters.AddWithValue("$at", Millis(cursor.At)); command.Parameters.AddWithValue("$id", cursor.Id); }
        if (filter.SenderUid is { } uid) { where += " AND e.uid=$uid"; command.Parameters.AddWithValue("$uid", uid); }
        if (filter.Nickname.Length > 0) { where += " AND instr(json_extract(e.after_data,'$.name'),$name)>0"; command.Parameters.AddWithValue("$name", filter.Nickname); }
        command.CommandText = $"SELECT e.id,e.session_id,e.received,e.source,e.kind,e.before_data,e.after_data FROM member_changes e JOIN sessions s ON s.id=e.session_id WHERE {where} ORDER BY e.received DESC,e.id DESC LIMIT 100";
        var result = new List<HistoryMemberChange>(100); using var reader = command.ExecuteReader();
        while (reader.Read()) if (JsonSerializer.Deserialize<HistoryMemberState>(reader.GetString(6), Json) is { } after)
            result.Add(new(reader.GetInt64(0), reader.GetString(1), Time(reader.GetInt64(2)), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<HistoryMemberState>(reader.GetString(5), Json), after));
        return result;
    }, token);
}
