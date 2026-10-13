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
            catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(_interval), token); }
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
        EventReceived?.Invoke(this, new LiveEvent(LiveEventType.Online, "系统", $"人气: {online}{(string.IsNullOrWhiteSpace(watched) ? "" : $" · {watched}")}", "#2196F3",
            Metadata: new Dictionary<string, object?> { ["room_summary"] = $"{state}{duration}{(string.IsNullOrWhiteSpace(title) ? "" : $" · {title}")}",
                ["popularity"] = online.ToString(), ["watched"] = watched, ["room_profile"] = profile }));
        if (room.TryGetProperty("uid", out var uidNode))
            await FetchRankAsync(uidNode.GetInt64(), token);
    }

    private async Task FetchRankAsync(long uid, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/xlive/general-interface/v1/rank/getOnlineGoldRank?ruid={uid}&roomId={_roomId}&page=1&pageSize=10");
        BiliHttp.SetHeaders(request, _cookies);
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var data = BiliHttp.ReadData(doc.RootElement);
        var items = data.TryGetProperty("OnlineRankItem", out var list) ? list : default;
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            EventReceived?.Invoke(this, new LiveEvent(LiveEventType.Rank, "系统", "暂无榜单", "#9C27B0",
                Metadata: new Dictionary<string, object?> { ["rank_total"] = BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(data, "onlineNum")) }));
            return;
        }
        var members = items.EnumerateArray().Take(10).Select((item, index) => BiliMessageAppearanceParser.RankMember(item, index + 1)).ToArray();
        var names = members.Select(member => $"{member.Rank}.{member.Username}").ToArray();
        EventReceived?.Invoke(this, new LiveEvent(LiveEventType.Rank, "系统", $"高能榜: {string.Join(" | ", names.Take(3))}", "#9C27B0",
            Metadata: new Dictionary<string, object?> { ["rank_names"] = names, ["rank_members"] = members,
                ["rank_total"] = BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(data, "onlineNum")) }));
    }

    public void Dispose() { _ = StopAsync(); _http.Dispose(); }
}
