using System.Text.Json;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

// Owned by the visible dashboard panel. Only one page is requested at a time.
public sealed class BiliDashboardRankService : IDisposable
{
    private readonly HttpClient _http = BiliHttp.CreateClient();
    public async Task<DashboardRankPage> FetchAsync(int room, long uid, DashboardRankKind kind, int page,
        CookieOptions cookies, CancellationToken token)
    {
        page = Math.Clamp(page, 1, 10);
        const int size = 30;
        var url = kind == DashboardRankKind.Energy
            ? $"https://api.live.bilibili.com/xlive/general-interface/v1/rank/getOnlineGoldRank?ruid={uid}&roomId={room}&page={page}&pageSize={size}"
            : $"https://api.live.bilibili.com/xlive/app-room/v2/guardTab/topListNew?ruid={uid}&roomid={room}&page={page}&page_size={size}&typ={kind switch { DashboardRankKind.GuardWeek => 3, DashboardRankKind.GuardMonth => 4, _ => 5 }}&platform=web";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        BiliHttp.SetHeaders(request, cookies);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var data = BiliHttp.ReadData(doc.RootElement);
        var rows = new List<LiveRankMember>(33);
        var info = BiliMessageAppearanceParser.Get(data, "info");
        var total = BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(kind == DashboardRankKind.Energy ? data : info,
            kind == DashboardRankKind.Energy ? "onlineNum" : "num"));
        if (kind != DashboardRankKind.Energy && page == 1) ReadRows("top3", 0);
        var length = ReadRows(kind == DashboardRankKind.Energy ? "OnlineRankItem" : "list", kind == DashboardRankKind.Energy ? (page - 1) * size : 3 + (page - 1) * size);
        var pages = BiliMessageAppearanceParser.Long(BiliMessageAppearanceParser.Get(info, "page"));
        var hasMore = kind == DashboardRankKind.Energy ? length == size && (total is null || page * size < total)
            : pages is > 0 ? page < pages : length == size;
        return new(rows.DistinctBy(row => row.Uid > 0 ? $"u{row.Uid}" : $"r{row.Rank}").ToArray(), hasMore, total is <= int.MaxValue ? (int?)total : null);

        int ReadRows(string key, int offset)
        {
            var list = BiliMessageAppearanceParser.Get(data, key);
            if (list.ValueKind != JsonValueKind.Array) return 0;
            var index = 0;
            foreach (var item in list.EnumerateArray().Take(size))
            {
                index++;
                var suppliedRank = BiliMessageAppearanceParser.Int(BiliMessageAppearanceParser.Get(item, kind == DashboardRankKind.Energy ? "userRank" : "rank"));
                rows.Add(BiliMessageAppearanceParser.RankMember(item, suppliedRank > 0 ? suppliedRank : offset + index));
            }
            return Math.Min(size, list.GetArrayLength());
        }
    }
    public void Dispose() => _http.Dispose();
}
