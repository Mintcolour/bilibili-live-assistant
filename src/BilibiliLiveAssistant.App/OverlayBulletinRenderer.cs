using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

internal static class OverlayBulletinRenderer
{
    private const int WidthLimit = 4096;
    private const int Gap = 14;
    internal const int PanelGap = 2;

    internal static int PanelRowHeight(OverlayRegionSettings region, DashboardSettings style)
    {
        var pad = region.Shadow ? Math.Max(4, region.ShadowOffset + region.ShadowSize + 2) : 4;
        var avatar = style.ShowAvatars ? Math.Min((int)Math.Round(style.AvatarSize * region.ContentScale), region.FontSize + 10) + (style.ShowIdentity ? 6 : 0) : 0;
        return Math.Max(36, (int)Math.Ceiling(Math.Max(region.FontSize * 1.5 + Math.Clamp(style.LineSpacing, 3, 10), avatar)) + pad * 2);
    }

    internal static IReadOnlyList<LiveAudienceMetric> AudienceMetrics(LiveEvent? rank, LiveEvent? online, bool enabled)
    {
        if (!enabled) return Array.Empty<LiveAudienceMetric>();
        var count = rank?.Extra.GetValueOrDefault("rank_total") as long?;
        var metrics = new List<LiveAudienceMetric>(2)
        { new(LiveAudienceMetricKind.EnergyMembers, count is >= 0 ? count.Value.ToString("N0") : "—") };
        if (online?.Extra.GetValueOrDefault("watched") is string { Length: > 0 } watched)
            metrics.Add(new(LiveAudienceMetricKind.Watched, watched.Replace("人看过", "", StringComparison.Ordinal).Trim()));
        return metrics;
    }

    public static LiveEvent MemberEvent(LiveRankMember member, string color) => new(LiveEventType.Danmaku,
        member.Username, "", color, member.Uid,
        Metadata: new Dictionary<string, object?> { ["rank_position"] = member.Rank }, Appearance: member.Appearance);

    public static IReadOnlyList<LiveEvent> CreateParts(IReadOnlyList<LiveAudienceMetric> metrics, IReadOnlyList<LiveRankMember> members,
        bool hasRank, OverlayRegionSettings region, DashboardSettings style, BiliImageCache cache, int height)
    {
        var parts = new List<LiveEvent>();
        var currentMetrics = metrics;
        var currentMembers = new List<LiveRankMember>();
        using var firstPrefix = Prefix(new(metrics, Array.Empty<LiveRankMember>(), hasRank), region, height, noMembers: members.Count == 0);
        var width = firstPrefix.Width;
        foreach (var member in members.Take(10))
        {
            using var preview = OverlayMessageRenderer.Render(MemberEvent(member, region.Color), region, style, cache, maxHeight: height);
            if (width + Gap + preview.Width > WidthLimit && (currentMembers.Count > 0 || currentMetrics.Count > 0))
            {
                AddPart(currentMembers.Count > 0);
                currentMetrics = Array.Empty<LiveAudienceMetric>(); currentMembers.Clear();
                using var prefix = Prefix(new(currentMetrics, Array.Empty<LiveRankMember>(), true), region, height, noMembers: false);
                width = prefix.Width;
            }
            currentMembers.Add(member); width += Gap + preview.Width;
        }
        if (currentMembers.Count > 0 || currentMetrics.Count > 0 || hasRank) AddPart(hasRank);
        return parts;

        void AddPart(bool showRank)
        {
            var bulletin = new LiveRoomBulletin(currentMetrics.ToArray(), currentMembers.ToArray(), showRank);
            parts.Add(new(LiveEventType.Rank, "系统", "高能榜播报", region.Color,
                Metadata: new Dictionary<string, object?> { ["room_bulletin"] = bulletin }));
        }
    }

    public static Bitmap Render(LiveRoomBulletin bulletin, OverlayRegionSettings region, DashboardSettings style, BiliImageCache cache, int height)
    {
        using var prefix = Prefix(bulletin, region, height, noMembers: bulletin.Members.Count == 0);
        var images = new List<Bitmap>(10);
        try
        {
            foreach (var member in bulletin.Members.Take(10))
                images.Add(OverlayMessageRenderer.Render(MemberEvent(member, region.Color), region, style, cache, maxHeight: height, background: false));
            var width = Math.Clamp(prefix.Width + images.Sum(image => Gap + image.Width), 24, WidthLimit);
            var rowHeight = Math.Max(prefix.Height, images.Count == 0 ? 0 : images.Max(image => image.Height));
            var result = new Bitmap(width, Math.Clamp(rowHeight, 36, Math.Max(36, height)), PixelFormat.Format32bppPArgb);
            try
            {
                using var graphics = Graphics.FromImage(result);
                OverlayMessageRenderer.DrawBackground(graphics, result.Size, region, OverlayTextRenderer.Color(region.Color));
                graphics.DrawImageUnscaled(prefix, 0, (result.Height - prefix.Height) / 2);
                var x = prefix.Width;
                foreach (var image in images)
                {
                    x += Gap; graphics.DrawImageUnscaled(image, x, (result.Height - image.Height) / 2); x += image.Width;
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally { foreach (var image in images) image.Dispose(); }
    }

    private static Bitmap Prefix(LiveRoomBulletin bulletin, OverlayRegionSettings region, int maxHeight, bool noMembers)
    {
        using var font = OverlayTextRenderer.CreateFont(region.FontFamily, region.FontSize, region.Bold, region.Italic);
        using var measureImage = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(measureImage);
        var iconSize = Math.Clamp(region.FontSize + 4, 20, 40);
        var pieces = new List<(int Icon, string Text, float X)>(3);
        float x = 4;
        foreach (var metric in bulletin.Metrics.Take(2))
        {
            var value = metric.Value.Length > 32 ? metric.Value[..32] + "…" : metric.Value;
            pieces.Add((metric.Kind == LiveAudienceMetricKind.Watched ? 0 : metric.Kind == LiveAudienceMetricKind.EnergyMembers ? 3 : 1, value, x));
            x += iconSize + 7 + OverlayTextRenderer.Measure(measure, value, font).Width + Gap;
        }
        if (bulletin.ShowRankHeader)
        {
            var label = noMembers ? "暂无榜单" : "高能榜";
            pieces.Add((2, label, x));
            x += iconSize + 7 + OverlayTextRenderer.Measure(measure, label, font).Width + 4;
        }
        var h = Math.Clamp((int)Math.Ceiling(Math.Max(iconSize, font.GetHeight(measure))) + 12, 36, Math.Max(36, maxHeight));
        var bitmap = new Bitmap(Math.Clamp((int)Math.Ceiling(x), 24, WidthLimit), h, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var piece in pieces)
            {
                var color = OverlayTextRenderer.Color(piece.Icon == 2 ? "#B998FF" : region.Color);
                DrawIcon(graphics, piece.Icon, new RectangleF(piece.X, (h - iconSize) / 2f, iconSize, iconSize), color, region.Outline);
                OverlayTextRenderer.Draw(graphics, piece.Text, font,
                    new PointF(piece.X + iconSize + 7, (h - font.GetHeight(graphics)) / 2), color, region.Outline, region);
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    // Vector icons remain visible without an emoji font, network or image decoding.
    private static void DrawIcon(Graphics graphics, int kind, RectangleF bounds, Color color, bool outline)
    {
        var state = graphics.Save();
        try
        {
            graphics.TranslateTransform(bounds.X, bounds.Y); graphics.ScaleTransform(bounds.Width / 24, bounds.Height / 24);
            using var shape = new GraphicsPath();
            if (kind == 0)
            {
                shape.AddBezier(2, 12, 7, 4, 17, 4, 22, 12);
                shape.AddBezier(22, 12, 17, 20, 7, 20, 2, 12); shape.CloseFigure();
                shape.AddEllipse(9, 9, 6, 6);
            }
            else if (kind == 1)
            {
                shape.AddBezier(12, 2, 13, 7, 6, 8, 7, 12);
                shape.AddBezier(7, 12, 6, 12, 5, 10, 5, 9);
                shape.AddBezier(5, 9, 1, 18, 7, 22, 12, 22);
                shape.AddBezier(12, 22, 22, 22, 23, 11, 17, 6);
                shape.AddBezier(17, 6, 18, 10, 14, 10, 12, 2); shape.CloseFigure();
            }
            else if (kind == 3)
            {
                shape.AddEllipse(7, 2, 8, 8); shape.StartFigure();
                shape.AddBezier(3, 22, 3, 9, 19, 9, 19, 22); shape.StartFigure();
                shape.AddArc(15, 4, 6, 6, -90, 180); shape.StartFigure();
                shape.AddBezier(18, 13, 22, 13, 23, 17, 23, 21);
            }
            else
            {
                shape.AddLines(new[] { new PointF(6, 3), new PointF(18, 3), new PointF(17, 11), new PointF(12, 15), new PointF(7, 11), new PointF(6, 3) });
                shape.AddBezier(6, 5, 0, 3, 1, 11, 7, 11); shape.StartFigure();
                shape.AddBezier(18, 5, 24, 3, 23, 11, 17, 11); shape.StartFigure();
                shape.AddLine(12, 15, 12, 20); shape.StartFigure();
                shape.AddLine(7, 21, 17, 21);
            }
            if (outline)
            {
                using var border = new Pen(Color.FromArgb(220, 16, 20, 28), 3.2f) { LineJoin = LineJoin.Round };
                graphics.DrawPath(border, shape);
            }
            using var pen = new Pen(color, 1.7f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawPath(pen, shape);
        }
        finally { graphics.Restore(state); }
    }
}
