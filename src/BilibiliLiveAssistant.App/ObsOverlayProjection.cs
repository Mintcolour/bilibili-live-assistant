using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

internal sealed record ObsImageReference(string Url, long Uid, int Limit, BiliImageRole Role);
internal sealed record ObsOverlayDocument(byte[] Json, IReadOnlyDictionary<string, ObsImageReference> Images);
internal sealed record ObsMessage(long Id, string Type, string Name, string Body, string Color, string Avatar, string Frame,
    string Medal, string MedalColor, string Guard, bool Admin, object[] Segments, double? Expires, string Price, object Effects,
    int GuardLevel, long? AmountMilliYuan, string GiftName, long? Quantity, int PriceTier);

internal sealed class ObsOverlayProjection
{
    private AppSettings? _settings;
    private LiveEvent? _rank;
    private (int Rank, LiveEvent Value)[] _ranking = [];
    private readonly Dictionary<long, (LiveEvent Value, ObsMessage Message, IReadOnlyDictionary<string, ObsImageReference> Images)> _messages = new();
    internal void Reset() { _settings = null; _rank = null; _ranking = []; _messages.Clear(); }
    internal ObsOverlayDocument Create(LiveAssistantViewModel vm, CompactTimeline timeline, long revision)
    {
        if (!ReferenceEquals(_settings, vm.Settings)) { _messages.Clear(); _settings = vm.Settings; _rank = null; }
        var used = new HashSet<long>(); Dictionary<string, ObsImageReference>? collected = null;
        var images = new Dictionary<string, ObsImageReference>(); var compact = vm.Settings.Compact with
        {
            ShowStatus = vm.Settings.Compact.ShowStatus && OverlayRegions.Enabled(vm.Settings.Display, OverlayRegionKind.Status),
            ShowGift = vm.Settings.Compact.ShowGift && OverlayRegions.Enabled(vm.Settings.Display, OverlayRegionKind.Gift),
            ShowSuperChat = vm.Settings.Compact.ShowSuperChat && OverlayRegions.Enabled(vm.Settings.Display, OverlayRegionKind.SuperChat),
            ShowEntry = vm.Settings.Compact.ShowEntry && OverlayRegions.Enabled(vm.Settings.Display, OverlayRegionKind.Interaction),
            ShowFollow = vm.Settings.Compact.ShowFollow && OverlayRegions.Enabled(vm.Settings.Display, OverlayRegionKind.Interaction),
        }; var style = vm.Settings.Dashboard;
        string Image(string? url, long uid = 0, BiliImageRole role = BiliImageRole.Content, int size = 128)
        {
            var normalized = BiliImageCache.NormalizeUrl(url ?? "")?.AbsoluteUri ?? "";
            if (normalized.Length == 0 && (uid <= 0 || role != BiliImageRole.Avatar)) return "";
            var reference = new ObsImageReference(normalized, uid, BiliImageCache.SizeTier(size), role);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{normalized}|{uid}|{reference.Limit}|{role}"))).ToLowerInvariant();
            if (collected is { Count: >= 16 } && !collected.ContainsKey(key)) return "";
            images.TryAdd(key, reference); collected?.TryAdd(key, reference); return "image/" + key;
        }
        ObsMessage? Message(CompactMessage? item, bool includeAvatar = true)
        {
            if (item is null) return null;
            used.Add(item.Id);
            if (_messages.TryGetValue(item.Id, out var cached) && ReferenceEquals(cached.Value, item.Value))
            {
                foreach (var pair in cached.Images) images.TryAdd(pair.Key, pair.Value);
                return cached.Message with { Expires = item.Expires?.ToUnixTimeMilliseconds() };
            }
            var messageImages = collected = new Dictionary<string, ObsImageReference>();
            var value = item.Value; var appearance = value.Appearance; var region = Region(vm.Settings, value);
            var avatar = includeAvatar && style.ShowsAvatar(value) ? Image(appearance?.AvatarUrl, value.Uid, BiliImageRole.Avatar, style.AvatarSize) : "";
            var frame = avatar.Length > 0 && style.ShowIdentity ? Image(appearance?.AvatarFrameUrl) : "";
            var body = value.Type == LiveEventType.SuperChat ? value.HistoryFields?.RawBody ?? value.Content : value.Content;
            var segments = RichMessageSegments.Split(body, appearance).Take(128).Select(segment => (object)new
            { text = segment.Text, image = segment.Emote is { } e ? Image(e.Url, size: BiliImageCache.SizeTier(
                    (appearance?.FullEmote is not null ? region.LargeEmoteHeight : compact.EmoteSize) * Math.Max(1d, e.Width / (double)Math.Max(1, e.Height)))) : "",
                width = segment.Emote?.Width ?? 0, height = segment.Emote?.Height ?? 0, large = appearance?.FullEmote is not null }).ToArray();
            var result = new ObsMessage(item.Id, LiveInteractions.IsFollow(value) ? "Follow" : LiveInteractions.IsLike(value) ? "Like" : value.Type.ToString(), value.Username, body, region.Color,
                avatar, frame, style.ShowMedals && appearance is { MedalLevel: > 0, MedalName.Length: > 0 } ? $"{appearance.MedalName} {appearance.MedalLevel}" : "",
                appearance?.MedalColor ?? "#527EBE", style.ShowIdentity ? appearance?.GuardLevel switch { 1 => "总督", 2 => "提督", 3 => "舰长", _ => "" } : "",
                style.ShowIdentity && appearance?.IsAdmin == true, segments, item.Expires?.ToUnixTimeMilliseconds(),
                SuperChatVisual.AmountMilliYuan(value) is { } amount ? $"¥{amount / 1000m:0.###}" : "",
                new { region.FontFamily, region.Bold, region.Italic, region.Outline, region.Shadow, region.ShadowOpacity, region.ShadowSize, region.ShadowOffset, region.ShadowColor, region.LargeEmoteHeight },
                appearance?.GuardLevel ?? 0, SuperChatVisual.AmountMilliYuan(value), value.HistoryFields?.GiftName ?? "",
                value.HistoryFields?.Quantity, PriceTier(vm.Settings, value));
            collected = null; _messages[item.Id] = (value, result, messageImages); return result;
        }
        var status = OverlayBulletinRenderer.AudienceMetrics(vm.LatestRank, vm.LatestOnline, true).Select(m => m.Value).ToArray();
        if (!ReferenceEquals(_rank, vm.LatestRank))
        {
            _rank = vm.LatestRank;
            _ranking = _rank is { } rank ? vm.GetRankMembers(rank, 3)
                .Select(m => (m.Rank, OverlayBulletinRenderer.MemberEvent(m, "#B998FF"))).ToArray() : [];
        }
        var ranks = _ranking.Select(m => new { rank = m.Rank, message = Message(new CompactMessage(-m.Rank, m.Value, DateTimeOffset.UtcNow), includeAvatar: false) }).ToArray();
        var hot = vm.Settings.HotPhrases;
        var hotPhrases = compact.ShowList && hot.Enabled && OverlayRegions.Enabled(vm.Settings.Display, OverlayRegionKind.Danmaku)
            ? vm.HotPhrases.Select(p => new { p.Text, p.Count, key = HotBubbleLayouts.Key(p), segments = RichMessageSegments.Split(p.Text, p.Appearance).Select(segment => new
                { text = segment.Text, image = segment.Emote is { } e ? Image(e.Url, size: BiliImageCache.SizeTier(Math.Max(hot.EmoteSize, hot.EmoteSize * Math.Clamp(e.Width / (double)Math.Max(1, e.Height), .25, 6)))) : "",
                    width = segment.Emote?.Width ?? 0, height = segment.Emote?.Height ?? 0 }).ToArray() }).ToArray() : [];
        var payload = new { revision, roomId = vm.RoomId, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), width = compact.ObsWidth, height = compact.ObsHeight,
            // CSS is delivered separately on changes; never repeat up to 256 KiB per message batch.
            config = Configuration(compact), style = Style(vm.Settings),
            rows = timeline.Rows.Select(item => Message(item)).ToArray(), sc = Message(timeline.SuperChat), gift = Message(timeline.Gift), entry = Message(timeline.Entry), follow = Message(timeline.Follow),
            hotPhrases, hotStyle = HotStyle(hot),
            status, ranks };
        foreach (var key in _messages.Keys.Where(k => !used.Contains(k)).ToArray()) _messages.Remove(key);
        return new(JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), images);
    }
    internal static object HotStyle(HotPhraseSettings hot) => new { hot.FontSize, hot.EmoteSize, hot.Opacity, hot.BackgroundOpacity, hot.Enabled, hot.AnimationEnabled, hot.AnimationDurationMs };
    internal static object Configuration(CompactOverlaySettings compact) => new
    {
        compact.FontSize, compact.EmoteSize, compact.Opacity, compact.BackgroundColor, compact.BackgroundOpacity,
        compact.SuperChatLines, compact.ShowStatus, compact.ShowSuperChat, compact.ShowGift, compact.ShowList, compact.ShowEntry, compact.ShowFollow,
        compact.ListAnimationEnabled, compact.ListAnimationDurationMs,
    };
    internal static object Style(AppSettings settings)
    {
        var s = settings.Dashboard; var danmaku = OverlayRegions.Get(settings.Display, OverlayRegionKind.Danmaku);
        return new
        {
            fontFamily = danmaku.FontFamily, s.ShowAvatars, s.ShowEntryAvatars, s.AvatarSize, s.ShowMedals, s.ShowIdentity, s.UsernameColor,
            danmaku.Shadow, danmaku.ShadowSize, danmaku.ShadowOpacity,
            regions = new[] { LiveEventType.Danmaku, LiveEventType.SuperChat, LiveEventType.Gift, LiveEventType.Guard, LiveEventType.Enter }
                .Select(type => { var r = Region(settings, new LiveEvent(type, "", "", "#FFFFFF")); return new
                { type = type.ToString(), r.FontFamily, r.Color, r.Bold, r.Italic, r.Outline, r.Shadow, r.ShadowColor, r.ShadowOffset, r.ShadowOpacity, r.ShadowSize, r.LargeEmoteHeight }; }).ToArray(),
        };
    }
    private static int PriceTier(AppSettings settings, LiveEvent value)
    {
        if (value.Type == LiveEventType.SuperChat) return SuperChatVisual.For(value).Tier;
        if (value.HistoryFields?.AmountMilliYuan is not { } amount) return 0;
        var tiers = value.Type == LiveEventType.SuperChat ? settings.PriceDurations.SuperChat : settings.PriceDurations.Gift;
        return tiers.Count(t => t.FromYuan <= amount / 1000m);
    }
    internal static OverlayRegionSettings Region(AppSettings settings, LiveEvent value)
    {
        var kind = value.Type switch { LiveEventType.Gift or LiveEventType.Guard => OverlayRegionKind.Gift,
            LiveEventType.SuperChat => OverlayRegionKind.SuperChat, LiveEventType.Enter => OverlayRegionKind.Interaction,
            LiveEventType.Online or LiveEventType.Rank => OverlayRegionKind.Status, _ => OverlayRegionKind.Danmaku };
        return OverlayRegions.ForRendering(OverlayRegions.Get(settings.Display, kind) with
        { FontSize = settings.Compact.FontSize, EmoteSize = settings.Compact.EmoteSize, ContentScale = 1,
            BackgroundColor = settings.Compact.BackgroundColor, BackgroundOpacity = settings.Compact.BackgroundOpacity, Opacity = settings.Compact.Opacity });
    }
}
