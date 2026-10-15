using System.Drawing;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

// Drawn into the alpha canvas: native child controls do not compose reliably in
// UpdateLayeredWindow windows. Geometry is shared by painting and hit testing.
internal static class OverlayInlineControls
{
    internal const int FieldHeight = 42;
    internal const int ModeHeight = 30;
    internal static int Columns(int width) => Math.Clamp((width - 16) / 112, 1, 3);
    internal static int HeaderHeight(int width) => width < 360 ? 60 : 36;
    internal static int ContentTop(int width) => HeaderHeight(width) + ModeHeight + (int)Math.Ceiling(3d / Columns(width)) * FieldHeight + 4;
    internal static Rectangle Mode(int width, int index, int count)
    {
        var cell = Math.Max(1, (width - 16) / count);
        return new(8 + index * cell, HeaderHeight(width) + 2, cell - 4, ModeHeight - 6);
    }
    internal static Rectangle Field(int width, int index)
    {
        var columns = Columns(width); var cell = Math.Max(1, (width - 16) / columns);
        return new(8 + index % columns * cell, HeaderHeight(width) + ModeHeight + index / columns * FieldHeight, cell - 6, FieldHeight - 2);
    }
    internal static Rectangle Minus(Rectangle field) => new(field.Right - 36, field.Top, 17, 18);
    internal static Rectangle Plus(Rectangle field) => new(field.Right - 18, field.Top, 17, 18);
    internal static Rectangle Track(Rectangle field) => new(field.Left + 4, field.Top + 24, Math.Max(1, field.Width - 8), 12);
    internal static (int Min, int Max, int Step) Range(int index) => index switch { 0 => (50, 200, 5), 1 => (12, 72, 1), _ => (10, 100, 5) };
    internal static int Value(OverlayRegionSettings settings, int index) => index switch
    { 0 => (int)Math.Round(settings.ContentScale * 100), 1 => settings.FontSize, _ => (int)Math.Round(settings.Opacity * 100) };
    internal static OverlayRegionSettings Set(OverlayRegionSettings settings, int index, int value)
    {
        var (min, max, _) = Range(index); value = Math.Clamp(value, min, max);
        return index switch { 0 => settings with { ContentScale = value / 100d }, 1 => settings with { FontSize = value }, _ => settings with { Opacity = value / 100d } };
    }
    internal static void Draw(Graphics graphics, int width, OverlayRegionSettings settings, OverlayRegionKind kind)
    {
        using var font = OverlayTextRenderer.CreateFont("Microsoft YaHei UI", 11);
        using var button = new SolidBrush(Color.FromArgb(69, 81, 100));
        using var line = new Pen(Color.FromArgb(101, 114, 136), 3);
        using var active = new Pen(Color.FromArgb(96, 193, 244), 3);
        using var thumb = new SolidBrush(Color.FromArgb(216, 240, 255));
        using var selected = new SolidBrush(Color.FromArgb(39, 112, 154));
        var modes = OverlayRegions.Modes(kind);
        for (var i = 0; i < modes.Length; i++)
        {
            var bounds = Mode(width, i, modes.Length);
            graphics.FillRectangle(settings.Mode == modes[i] ? selected : button, bounds);
            var title = kind == OverlayRegionKind.Interaction ? "固定停留" : modes[i] switch
            { OverlayDisplayMode.Scrolling => "滚动", OverlayDisplayMode.List => "列表", OverlayDisplayMode.Grid => "卡片", OverlayDisplayMode.Cards => "固定卡片", _ => "固定面板" };
            var modeState = graphics.Save(); graphics.SetClip(bounds, System.Drawing.Drawing2D.CombineMode.Intersect);
            OverlayTextRenderer.Draw(graphics, title, font, new(bounds.Left + 6, bounds.Top + 4), Color.White, false); graphics.Restore(modeState);
        }
        for (var index = 0; index < 3; index++)
        {
            var field = Field(width, index); var value = Value(settings, index); var (min, max, _) = Range(index);
            var label = index switch { 0 => $"大小 {value}%", 1 => $"字号 {value}", _ => $"不透明 {value}%" };
            var state = graphics.Save(); graphics.SetClip(new Rectangle(field.Left, field.Top, Math.Max(1, field.Width - 37), 20), System.Drawing.Drawing2D.CombineMode.Intersect);
            OverlayTextRenderer.Draw(graphics, label, font, field.Location, Color.White, false); graphics.Restore(state);
            foreach (var bounds in new[] { Minus(field), Plus(field) }) graphics.FillRectangle(button, bounds);
            OverlayTextRenderer.Draw(graphics, "−", font, new(Minus(field).X + 3, field.Top), Color.White, false);
            OverlayTextRenderer.Draw(graphics, "+", font, new(Plus(field).X + 3, field.Top), Color.White, false);
            var track = Track(field); var y = track.Top + 5; var x = track.Left + (track.Width - 1) * (value - min) / (double)(max - min);
            graphics.DrawLine(line, track.Left, y, track.Right - 1, y);
            graphics.DrawLine(active, track.Left, y, (float)x, y); graphics.FillEllipse(thumb, (float)x - 4, y - 4, 8, 8);
        }
    }
}
