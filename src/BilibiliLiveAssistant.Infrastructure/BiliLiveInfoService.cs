using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

public sealed class BiliLiveInfoService : ILiveInfoService, IDisposable
{
    private readonly HttpClient _http;
    private readonly BiliWbiSigner _signer;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private int _roomId;
    private CookieOptions _cookies = new();
    private int _interval;
    private readonly BiliDashboardRankService _ranks = new();
    private HistoryAnalysisSettings _analysis = new();
    private bool _archive = true;
    private DateTimeOffset _countsDue, _energyDue, _guardsDue;
    public void ConfigureAnalysis(HistoryAnalysisSettings settings, bool enabled) { _analysis = settings.Normalize(); _archive = enabled; }

    public BiliLiveInfoService(HttpClient? http = null)
    {
        _http = http ?? BiliHttp.CreateClient();
        _signer = new BiliWbiSigner(_http);
    }
    public event EventHandler<LiveEvent>? EventReceived;

    public Task StartAsync(int roomId, CookieOptions cookies, int intervalSeconds, CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return Task.CompletedTask;
        _roomId = roomId;
        _cookies = cookies;
        _interval = Math.Clamp(intervalSeconds, 5, 600);
        _countsDue = _energyDue = _guardsDue = default;
        _signer.Reset();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;
        _cts = cts;
        _task = Task.Run(() => PollLoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        cts.Cancel();
        if (_task is not null) { try { await _task; } catch { } }
        _task = null;
        cts.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await FetchAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch { if (_archive && DateTimeOffset.UtcNow >= _countsDue) { EmitSample(new(HistorySampleKind.Counts, DateTimeOffset.UtcNow, _analysis.CountsSeconds, null, null, null, null, null, [], 0, null, "采集失败")); _countsDue = DateTimeOffset.UtcNow.AddSeconds(_analysis.CountsSeconds); } }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(_interval, Math.Min(_analysis.GuardSeconds, Math.Min(_analysis.CountsSeconds, _analysis.EnergySeconds)))), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private async Task FetchAsync(CancellationToken token)
    {
        var parameters = await _signer.SignAsync(new Dictionary<string, string> { ["room_id"] = _roomId.ToString() }, token, _cookies);
        var query = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?{query}");
        BiliHttp.SetHeaders(request, _cookies);
        using var response = await _http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var data = BiliHttp.ReadData(doc.RootElement);
        var room = data.GetProperty("room_info");
        var watched = data.TryGetProperty("watched_show", out var watchedNode) && watchedNode.TryGetProperty("text_large", out var text)
            ? text.GetString() ?? string.Empty : string.Empty;
        var online = room.TryGetProperty("online", out var onlineNode) ? onlineNode.GetInt32() : 0;
        var live = room.TryGetProperty("live_status", out var liveNode) ? liveNode.GetInt32() : -1;
        var title = room.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : null;
        var anchor = BiliMessageAppearanceParser.Get(data, "anchor_info");
        var basic = BiliMessageAppearanceParser.Get(anchor, "base_info");
        var medal = BiliMessageAppearanceParser.Get(anchor, "medal_info");
        var profile = new LiveRoomProfile(
            BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(room, "uid")) ?? 0,
            BiliMessageAppearanceParser.Int(BiliMessageAppearanceParser.Get(room, "room_id")) is > 0 and var actualRoom ? actualRoom : _roomId,
            BiliMessageAppearanceParser.Text(BiliMessageAppearanceParser.Get(basic, "uname")),
            BiliMessageAppearanceParser.Text(BiliMessageAppearanceParser.Get(basic, "face")), title ?? "",
            BiliMessageAppearanceParser.Text(BiliMessageAppearanceParser.Get(medal, "medal_name")),
            BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(medal, "fansclub")),
            BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(BiliMessageAppearanceParser.Get(data, "guard_info"), "count")),
            BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(BiliMessageAppearanceParser.Get(data, "like_info_v3"), "total_likes")), null);
        var state = live switch { 1 => "直播中", 2 => "轮播中", 0 => "未开播", _ => "状态暂无数据" };
        var duration = string.Empty;
        if (live == 1 && room.TryGetProperty("live_start_time", out var startNode) && startNode.TryGetInt64(out var start) && start > 0 && start < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            var elapsed = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(start);
            duration = $" · 已直播 {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
        token.ThrowIfCancellationRequested();
        EventReceived?.Invoke(this, new LiveEvent(LiveEventType.Online, "系统", $"人气: {online}{(string.IsNullOrWhiteSpace(watched) ? "" : $" · {watched}")}", "#2196F3",
            Metadata: new Dictionary<string, object?> { ["room_summary"] = $"{state}{duration}{(string.IsNullOrWhiteSpace(title) ? "" : $" · {title}")}",
                ["popularity"] = online.ToString(), ["watched"] = watched, ["room_profile"] = profile,
                ["archive_popularity"] = BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(room, "online")),
                ["archive_watched"] = BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(BiliMessageAppearanceParser.Get(data, "watched_show"), "num")) }));
        if (_archive && DateTimeOffset.UtcNow >= _countsDue)
        {
            EmitSample(new(HistorySampleKind.Counts, DateTimeOffset.UtcNow, _analysis.CountsSeconds, profile.FansClub, profile.Guards, null,
                BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(room, "online")),
                BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(BiliMessageAppearanceParser.Get(data, "watched_show"), "num")), [], 0, null));
            _countsDue = DateTimeOffset.UtcNow.AddSeconds(_analysis.CountsSeconds);
        }
        if (profile.Uid > 0)
        {
            await CollectRankAsync(profile.Uid, false, token);
            if (_archive && DateTimeOffset.UtcNow >= _guardsDue) await CollectRankAsync(profile.Uid, true, token);
        }
    }

    private void EmitSample(HistoryAnalysisSample sample) => EventReceived?.Invoke(this,
        new LiveEvent(LiveEventType.Rank, "系统", "", "#FFFFFF", Metadata: new Dictionary<string, object?> { ["archive_analysis"] = sample }));

    private async Task CollectRankAsync(long uid, bool guard, CancellationToken token)
    {
        var settings = _analysis;
        var collect = _archive && (guard || DateTimeOffset.UtcNow >= _energyDue);
        var limit = guard ? settings.GuardLimit : collect ? settings.EnergyLimit : 10;
        var interval = guard ? settings.GuardSeconds : settings.EnergySeconds;
        var kind = guard ? HistorySampleKind.Guards : HistorySampleKind.Energy;
        var rows = new List<LiveRankMember>(limit); long? total = null; var at = DateTimeOffset.UtcNow;
        var status = "ok";
        try
        {
            for (var page = 1; page <= 10 && rows.Count < limit; page++)
            {
                var result = await _ranks.FetchAsync(_roomId, uid, guard ? DashboardRankKind.GuardTotal : DashboardRankKind.Energy, page, _cookies, token);
                if (page == 1) { at = result.SampledAt; total = result.Total; }
                foreach (var member in result.Members)
                    if (rows.Count < limit && (member.Uid <= 0 || rows.All(r => r.Uid != member.Uid))) rows.Add(member);
                if (!result.HasMore) break;
            }
            token.ThrowIfCancellationRequested();
            if (!guard) EventReceived?.Invoke(this, new LiveEvent(LiveEventType.Rank, "系统", rows.Count == 0 ? "暂无榜单" : "高能榜: " + string.Join(" | ", rows.Take(3).Select(m => $"{m.Rank}.{m.Username}")), "#9C27B0",
                Metadata: new Dictionary<string, object?> { ["rank_members"] = rows.Take(10).ToArray(), ["rank_names"] = rows.Take(10).Select(m => m.Username).ToArray(), ["rank_total"] = total }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { status = rows.Count > 0 ? "分页失败，部分采集" : "采集失败"; }
        if (collect)
        {
            EmitSample(new(kind, at, interval, null, null, guard ? null : total, null, null, rows.ToArray(), limit, total, status));
            if (guard) _guardsDue = DateTimeOffset.UtcNow.AddSeconds(interval); else _energyDue = DateTimeOffset.UtcNow.AddSeconds(interval);
        }
    }

    public void Dispose() { _ = StopAsync(); _ranks.Dispose(); _http.Dispose(); }
}
