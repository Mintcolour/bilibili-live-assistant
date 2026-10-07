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
        _http = http ?? new HttpClient();
        _signer = new BiliWbiSigner(_http);
    }
    public event EventHandler<LiveEvent>? EventReceived;

    public Task StartAsync(int roomId, CookieOptions cookies, int intervalSeconds, CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return Task.CompletedTask;
        _roomId = roomId;
        _cookies = cookies;
        _interval = Math.Clamp(intervalSeconds, 5, 600);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _task = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
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
            await Task.Delay(TimeSpan.FromSeconds(_interval), token);
        }
    }

    private async Task FetchAsync(CancellationToken token)
    {
        var parameters = await _signer.SignAsync(new Dictionary<string, string> { ["room_id"] = _roomId.ToString() }, token);
        var query = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?{query}");
        request.Headers.Add("Cookie", $"SESSDATA={_cookies.Sessdata}; buvid3={_cookies.Buvid3}");
        using var response = await _http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var data = doc.RootElement.GetProperty("data");
        var room = data.GetProperty("room_info");
        var watched = data.TryGetProperty("watched_show", out var watchedNode) && watchedNode.TryGetProperty("text_large", out var text)
            ? text.GetString() ?? string.Empty : string.Empty;
        var online = room.TryGetProperty("online", out var onlineNode) ? onlineNode.GetInt32() : 0;
        EventReceived?.Invoke(this, new LiveEvent(LiveEventType.Online, "系统", $"人气: {online}{(string.IsNullOrWhiteSpace(watched) ? "" : $" · {watched}")}", "#2196F3"));
        if (room.TryGetProperty("uid", out var uidNode))
            await FetchRankAsync(uidNode.GetInt64(), token);
    }

    private async Task FetchRankAsync(long uid, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/xlive/general-interface/v1/rank/getOnlineGoldRank?ruid={uid}&roomId={_roomId}&page=1&pageSize=10");
        request.Headers.Add("Cookie", $"SESSDATA={_cookies.Sessdata}; buvid3={_cookies.Buvid3}");
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var items = doc.RootElement.GetProperty("data").TryGetProperty("OnlineRankItem", out var list) ? list : default;
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return;
        var names = items.EnumerateArray().Take(3).Select((item, index) => $"{index + 1}.{item.GetProperty("name").GetString()}");
        EventReceived?.Invoke(this, new LiveEvent(LiveEventType.Rank, "系统", $"高能榜: {string.Join(" | ", names)}", "#9C27B0"));
    }

    public void Dispose() { _ = StopAsync(); _http.Dispose(); }
}
