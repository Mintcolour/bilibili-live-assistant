using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

internal static class OverlayTextRenderer
{
    // Fill and outline share identical glyph geometry; no rectangle can reject only the fill.
    public static void Draw(Graphics graphics, string text, Font font, PointF location, Color color, bool outline, OverlayRegionSettings? effects = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (HasEmoji(text))
        {
            using var emoji = CreateFont("Segoe UI Emoji", font.Size);
            using var emojiFormat = Format();
            foreach (var run in Runs(text))
            {
                var selected = run.Emoji ? emoji : font;
                if (run.Emoji) DrawEmoji(graphics, run.Text, selected, location, color, outline, effects, emojiFormat);
                else Draw(graphics, run.Text, selected, location, color, outline, effects);
                location.X += graphics.MeasureString(run.Text, selected, PointF.Empty, emojiFormat).Width;
            }
            return;
        }
        PerformanceCounters.Text(false);
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
        PerformanceCounters.Text(true);
        using var format = Format();
        if (HasEmoji(text))
        {
            using var emoji = CreateFont("Segoe UI Emoji", font.Size);
            var width = 0f; var height = 0f;
            foreach (var run in Runs(text))
            {
                var size = graphics.MeasureString(run.Text, run.Emoji ? emoji : font, PointF.Empty, format);
                width += size.Width; height = Math.Max(height, size.Height);
            }
            return new(width, height);
        }
        return graphics.MeasureString(text, font, PointF.Empty, format);
    }
    private static bool IsEmoji(Rune rune) => rune.Value is >= 0x1F000 and <= 0x1FAFF
        or >= 0x2300 and <= 0x23FF or >= 0x2600 and <= 0x27BF or 0xFE0F or 0x20E3 or 0x00A9 or 0x00AE or 0x3030 or 0x303D;
    private static bool HasEmoji(string text) { foreach (var rune in text.EnumerateRunes()) if (IsEmoji(rune)) return true; return false; }
    private static IEnumerable<(string Text, bool Emoji)> Runs(string text)
    {
        // Keep modifiers, flags, variation selectors and ZWJ families together.
        var elements = StringInfo.GetTextElementEnumerator(text); var start = 0; var end = 0; bool? emoji = null;
        while (elements.MoveNext())
        {
            var part = elements.GetTextElement(); var kind = HasEmoji(part);
            if (emoji is not null && kind != emoji)
            { yield return (text[start..end], emoji.Value); start = end; }
            emoji = kind; end = elements.ElementIndex + part.Length;
        }
        if (emoji is not null) yield return (text[start..end], emoji.Value);
    }
    private static void DrawEmoji(Graphics graphics, string text, Font font, PointF point, Color color, bool outline, OverlayRegionSettings? effects, StringFormat format)
    {
        PerformanceCounters.Text(false);
        // AddString uses one font's glyph outlines and cannot perform text
        // shaping/fallback. GDI+ DrawString shapes an entire emoji sequence and
        // writes premultiplied alpha on the existing transparent sprite.
        if (effects is { Shadow: true, ShadowOpacity: > 0 })
        {
            using var shadow = new SolidBrush(Color(effects.ShadowColor, effects.ShadowOpacity * color.A / 255d));
            graphics.DrawString(text, font, shadow, new PointF(point.X + effects.ShadowOffset, point.Y + effects.ShadowOffset), format);
        }
        if (outline)
        {
            using var edge = new SolidBrush(System.Drawing.Color.FromArgb(color.A, 16, 20, 28));
            foreach (var offset in new[] { new PointF(-1, 0), new(1, 0), new(0, -1), new(0, 1) })
                graphics.DrawString(text, font, edge, new PointF(point.X + offset.X, point.Y + offset.Y), format);
        }
        using var fill = new SolidBrush(color); graphics.DrawString(text, font, fill, point, format);
    }
    private static StringFormat Format()
    {
        // This getter already creates an owned native format. Cloning it left
        // the original for finalization on every glyph measurement/draw.
        var format = StringFormat.GenericTypographic;
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
