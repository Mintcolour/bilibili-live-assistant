using System.Drawing;
using System.Drawing.Imaging;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

// Local samples only. Movement reuses cached bitmaps and the shared frame clock;
// samples never enter the live pipeline or start image/media requests.
internal sealed class OverlayEditorPreview : IDisposable
{
    private sealed class SampleImage(Bitmap image, int x, int y)
    {
        internal readonly Bitmap Image = image;
        internal double X = x;
        internal readonly int Y = y;
        internal readonly PointF[] Destination = new PointF[3];
    }
    private readonly List<SampleImage> _images = new();
    internal bool IsScrolling { get; private set; }
    internal void Rebuild(OverlayRegionKind kind, OverlayRegionSettings region, DashboardSettings style,
        BiliImageCache cache, int width, int height, int top)
    {
        Dispose();
        var available = height - top - 8; if (available < 24 || width < 32) return;
        var stationary = region.Mode != OverlayDisplayMode.Scrolling || kind == OverlayRegionKind.Interaction;
        IsScrolling = !stationary;
        var columns = region.Mode == OverlayDisplayMode.Grid ? Math.Max(1, Math.Min(region.CardColumns, (width - 8) / 208)) : 1;
        var cellWidth = Math.Max(24, (width - 16 - (columns - 1) * 8) / columns);
        var gap = stationary && kind == OverlayRegionKind.Status ? OverlayBulletinRenderer.PanelGap : kind == OverlayRegionKind.Interaction ? 0 : 8;
        var cellHeight = stationary && kind is not (OverlayRegionKind.Status or OverlayRegionKind.Interaction)
            ? Math.Max(region.CardHeight, region.FontSize * 3 + 24)
            : kind == OverlayRegionKind.Status && stationary ? OverlayBulletinRenderer.PanelRowHeight(region, style)
            : Math.Max(region.FontSize * 2 + 12, region.EmoteSize + 8);
        var count = Math.Clamp((available + gap) / (cellHeight + gap) * columns, 1, 8);
        if (kind == OverlayRegionKind.Interaction) count = Math.Min(count, Math.Min(3, region.MaxVisible));
        if (region.Mode == OverlayDisplayMode.Cards) count = Math.Min(count, region.MaxVisible);
        var messages = Samples(kind, region, stationary);
        count = Math.Min(count, kind == OverlayRegionKind.Status ? messages.Length : region.Mode == OverlayDisplayMode.Grid ? 8 : 3);
        long bytes = 0;
        try
        {
            for (var slot = 0; slot < count; slot++)
            {
                var x = 8 + slot % columns * (cellWidth + 8); var y = top + slot / columns * (cellHeight + gap);
                var visibleHeight = Math.Min(cellHeight, height - y - 8);
                var reservedWidth = stationary || kind == OverlayRegionKind.Interaction ? cellWidth : 4096;
                visibleHeight = (int)Math.Min(visibleHeight, (16 * 1024 * 1024 - bytes) / Math.Max(1, reservedWidth * 4L));
                if (visibleHeight < 36) break;
                var message = messages[slot % messages.Length];
                var effective = message.Type is LiveEventType.Gift or LiveEventType.Guard && stationary ? region with { Bold = true } : region;
                var renderStyle = stationary ? style with { TextColor = region.Color, TextOpacity = 1 } : style;
                var body = OverlayMessageRenderer.Render(message, effective, renderStyle, cache,
                    width: stationary || kind == OverlayRegionKind.Interaction ? cellWidth : 0, maxHeight: visibleHeight,
                    card: stationary && kind != OverlayRegionKind.Status, background: !stationary);
                Bitmap image;
                if (stationary)
                {
                    using (body)
                    {
                        image = new Bitmap(cellWidth, Math.Max(36, visibleHeight), PixelFormat.Format32bppPArgb);
                        try
                        {
                            using var graphics = Graphics.FromImage(image);
                            OverlayMessageRenderer.DrawBackground(graphics, image.Size, effective, OverlayTextRenderer.Color(region.Color));
                            graphics.DrawImageUnscaled(body, 0, 0);
                            if (region.Mode == OverlayDisplayMode.Grid)
                            {
                                using var countFont = OverlayTextRenderer.CreateFont("Microsoft YaHei UI", 12);
                                OverlayTextRenderer.Draw(graphics, $"×{slot + 2}", countFont, new PointF(8, Math.Max(8, image.Height - 22)), Color.LightSkyBlue, false);
                            }
                        }
                        catch { image.Dispose(); throw; }
                    }
                }
                else image = body;
                if ((bytes += (long)image.Width * image.Height * 4) > 16 * 1024 * 1024) { image.Dispose(); break; }
                _images.Add(new(image, x, y));
            }
        }
        catch { Dispose(); throw; }
        IsScrolling = !stationary && _images.Count > 0;
    }
    private static LiveEvent[] Samples(OverlayRegionKind kind, OverlayRegionSettings region, bool stationary)
    {
        // Renderer supplies its local avatar silhouette; badges are drawn locally.
        var appearance = new LiveMessageAppearance { MedalName = "示例牌", MedalLevel = 21, MedalColor = "#4388BC", GuardLevel = 3 };
        LiveEvent Sample(LiveEventType type, string name, string content, int uid = 1) => new(type, name, content, region.Color, uid,
            Metadata: new Dictionary<string, object?> { ["sample"] = true }, Appearance: appearance);
        if (kind == OverlayRegionKind.Status)
        {
            var members = Enumerable.Range(1, region.RankCount).Select(i => new LiveRankMember(i, i, $"示例观众{i}", appearance)).ToArray();
            var metrics = region.ShowAudienceStats ? new[] { new LiveAudienceMetric(LiveAudienceMetricKind.EnergyMembers, "8,747"), new LiveAudienceMetric(LiveAudienceMetricKind.Watched, "1.2万") } : [];
            if (!stationary) return [new(LiveEventType.Rank, "", "", region.Color,
                Metadata: new Dictionary<string, object?> { ["room_bulletin"] = new LiveRoomBulletin(metrics, members, false) })];
            return (metrics.Length > 0 ? new[] { new LiveEvent(LiveEventType.Rank, "", "", region.Color,
                Metadata: new Dictionary<string, object?> { ["room_bulletin"] = new LiveRoomBulletin(metrics, [], false) }) } : [])
                .Concat(members.Select(m => OverlayBulletinRenderer.MemberEvent(m, region.Color))).ToArray();
        }
        return kind switch
        {
            OverlayRegionKind.Gift => [Sample(LiveEventType.Gift, "礼物示例", "赠送 小心心 × 3"), Sample(LiveEventType.Guard, "上舰示例", "开通 舰长 × 1", 2)],
            OverlayRegionKind.SuperChat => [Sample(LiveEventType.SuperChat, "SC 示例", "¥30 这是一条醒目留言，谢谢陪伴 😊")],
            OverlayRegionKind.Interaction => [Sample(LiveEventType.Enter, "新关注示例", "关注了主播") with { HistoryFields = new(InteractionKind: 2) },
                Sample(LiveEventType.Enter, "特别关注示例", "特别关注了主播", 2) with { HistoryFields = new(InteractionKind: 4) },
                Sample(LiveEventType.Enter, "进房示例", "进入了直播间", 3)],
            _ => [Sample(LiveEventType.Danmaku, "弹幕示例", "这是一条普通弹幕，预览字体和大小 😊"), Sample(LiveEventType.Danmaku, "观众示例", "一起享受直播时光！", 2)],
        };
    }
    internal void Draw(Graphics graphics, double opacity)
    {
        using var attributes = new ImageAttributes(); attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)opacity });
        foreach (var sample in _images)
        {
            var image = sample.Image; var points = sample.Destination; var x = (float)sample.X;
            points[0] = new(x, sample.Y); points[1] = new(x + image.Width, sample.Y); points[2] = new(x, sample.Y + image.Height);
            graphics.DrawImage(image, points, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
        }
    }
    internal void Advance(double elapsed, int width, int speed, Action<Rectangle> invalidate)
    {
        if (!IsScrolling) return;
        foreach (var sample in _images)
        {
            var oldX = sample.X; sample.X -= speed * elapsed;
            if (sample.X + sample.Image.Width < 0) sample.X = width + 16;
            var left = (int)Math.Floor(Math.Min(oldX, sample.X)) - 2;
            var right = (int)Math.Ceiling(Math.Max(oldX, sample.X)) + sample.Image.Width + 2;
            invalidate(new Rectangle(left, sample.Y, right - left, sample.Image.Height));
        }
    }
    public void Dispose() { foreach (var item in _images) item.Image.Dispose(); _images.Clear(); IsScrolling = false; }
}
