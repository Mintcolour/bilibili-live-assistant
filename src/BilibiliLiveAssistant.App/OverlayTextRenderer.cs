using System.Drawing;
using System.Drawing.Drawing2D;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

internal static class OverlayTextRenderer
{
    // Fill and outline share identical glyph geometry; no rectangle can reject only the fill.
    public static void Draw(Graphics graphics, string text, Font font, PointF location, Color color, bool outline, OverlayRegionSettings? effects = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using var format = Format();
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.SizeInPoints * graphics.DpiY / 72f, location, format);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (effects is { Shadow: true, ShadowOpacity: > 0 })
        {
            var state = graphics.Save();
            graphics.TranslateTransform(effects.ShadowOffset, effects.ShadowOffset);
            var shadow = Color(effects.ShadowColor, effects.ShadowOpacity * color.A / 255d);
            if (effects.ShadowSize > 0)
            {
                using var pen = new Pen(System.Drawing.Color.FromArgb(shadow.A / 2, shadow), effects.ShadowSize * 2f) { LineJoin = LineJoin.Round };
                graphics.DrawPath(pen, path);
            }
            using var shade = new SolidBrush(shadow); graphics.FillPath(shade, path);
            graphics.Restore(state);
        }
        if (outline)
        {
            using var pen = new Pen(System.Drawing.Color.FromArgb(color.A, 16, 20, 28), Math.Clamp(font.Size / 22f, 0.9f, 1.6f)) { LineJoin = LineJoin.Round };
            graphics.DrawPath(pen, path);
        }
        using var brush = new SolidBrush(color); graphics.FillPath(brush, path);
    }
    public static SizeF Measure(Graphics graphics, string text, Font font)
    {
        using var format = Format();
        return graphics.MeasureString(text, font, PointF.Empty, format);
    }
    private static StringFormat Format()
    {
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
        return format;
    }
    public static Font CreateFont(string family, float size, bool bold = false, bool italic = false)
    {
        try { return new Font(family, size, (bold ? FontStyle.Bold : FontStyle.Regular) | (italic ? FontStyle.Italic : FontStyle.Regular), GraphicsUnit.Pixel); }
        catch (ArgumentException) { return new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel); }
    }
    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(0, radius) * 2);
        if (diameter <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
    public static Color Color(string hex, double opacity = 1)
    {
        var parsed = ColorTranslator.FromHtml(BilibiliLiveAssistant.Core.OverlayRegionSettings.IsColor(hex) ? hex : "#FFFFFF");
        return System.Drawing.Color.FromArgb((int)Math.Clamp(Math.Round(opacity * 255), 0, 255), parsed);
    }
}
