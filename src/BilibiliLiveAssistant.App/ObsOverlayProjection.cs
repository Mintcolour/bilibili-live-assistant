using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

internal sealed record ObsImageReference(string Url, long Uid, int Limit, BiliImageRole Role);
internal sealed record ObsOverlayDocument(byte[] Json, IReadOnlyDictionary<string, ObsImageReference> Images);
internal sealed record ObsMessage(long Id, string Type, string Name, string Body, string Color, string Avatar, string Frame,
    string Medal, string MedalColor, string Guard, bool Admin, object[] Segments, double? Expires, string Price, object Effects);

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
        ObsMessage? Message(CompactMessage? item)
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
            var avatar = style.ShowsAvatar(value) ? Image(appearance?.AvatarUrl, value.Uid, BiliImageRole.Avatar, style.AvatarSize) : "";
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
                value.HistoryFields?.AmountMilliYuan is { } amount ? $"¥{amount / 1000m:0.###}" : "",
                new { region.FontFamily, region.Bold, region.Italic, region.Outline, region.Shadow, region.ShadowOpacity, region.ShadowSize, region.ShadowOffset, region.ShadowColor, region.LargeEmoteHeight });
            collected = null; _messages[item.Id] = (value, result, messageImages); return result;
        }
        var status = OverlayBulletinRenderer.AudienceMetrics(vm.LatestRank, vm.LatestOnline, true).Select(m => m.Value).ToArray();
        if (!ReferenceEquals(_rank, vm.LatestRank))
        {
            _rank = vm.LatestRank;
            _ranking = _rank is { } rank ? vm.GetRankMembers(rank, OverlayRegions.Get(vm.Settings.Display, OverlayRegionKind.Status).RankCount)
                .Select(m => (m.Rank, OverlayBulletinRenderer.MemberEvent(m, "#B998FF"))).ToArray() : [];
        }
        var ranks = _ranking.Select(m => new { rank = m.Rank, message = Message(new CompactMessage(-m.Rank, m.Value, DateTimeOffset.UtcNow)) }).ToArray();
        var payload = new { revision, roomId = vm.RoomId, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), width = compact.ObsWidth, height = compact.ObsHeight,
            config = compact, style = new { fontFamily = OverlayRegions.Get(vm.Settings.Display, OverlayRegionKind.Danmaku).FontFamily,
                style.ShowAvatars, style.AvatarSize, style.ShowMedals, style.ShowIdentity, style.UsernameColor,
                shadow = OverlayRegions.Get(vm.Settings.Display, OverlayRegionKind.Danmaku).Shadow,
                shadowSize = OverlayRegions.Get(vm.Settings.Display, OverlayRegionKind.Danmaku).ShadowSize,
                shadowOpacity = OverlayRegions.Get(vm.Settings.Display, OverlayRegionKind.Danmaku).ShadowOpacity },
            rows = timeline.Rows.Select(Message).ToArray(), sc = Message(timeline.SuperChat), gift = Message(timeline.Gift), entry = Message(timeline.Entry), follow = Message(timeline.Follow),
            status, ranks };
        foreach (var key in _messages.Keys.Where(k => !used.Contains(k)).ToArray()) _messages.Remove(key);
        return new(JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), images);
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
