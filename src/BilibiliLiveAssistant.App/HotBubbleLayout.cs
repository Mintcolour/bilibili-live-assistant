using System.Drawing;
using System.Globalization;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

internal sealed record HotBubblePiece(string Text, LiveEmote? Emote, float Width);
internal sealed record HotBubbleLayout(HotPhrase Phrase, Rectangle Bounds, IReadOnlyList<HotBubblePiece> Pieces, float CountWidth);
internal static class HotBubbleLayouts
{
    internal static string Key(HotPhrase value) => value.Key.Length > 0 ? value.Key : value.Text;
    internal static int RowHeight(HotPhraseSettings style) => Math.Max(style.EmoteSize, (int)Math.Ceiling(style.FontSize * 1.5)) + 18;
    internal static IReadOnlyList<HotBubbleLayout> Create(IReadOnlyList<HotPhrase> items, HotPhraseSettings style,
        int width, int height, int maxRows, string family)
    {
        var result = new List<HotBubbleLayout>(5);
        if (!style.Enabled || width < 80 || height < RowHeight(style)) return result;
        using var bitmap = new Bitmap(1, 1); using var g = Graphics.FromImage(bitmap);
        using var font = OverlayTextRenderer.CreateFont(family, style.FontSize);
        var rowHeight = RowHeight(style); var rows = Math.Min(maxRows, height / rowHeight); var x = 0; var row = 0;
        foreach (var phrase in items.Take(5))
        {
            var countWidth = OverlayTextRenderer.Measure(g, "×" + phrase.Count, font).Width + 12;
            var segments = RichMessageSegments.Split(phrase.Text, phrase.Appearance).ToArray();
            float Measure(MessageSegment p) => p.Emote is { } e ? Math.Clamp(style.EmoteSize * e.Width / (float)Math.Max(1, e.Height), 12, 160) + 3
                : OverlayTextRenderer.Measure(g, p.Text, font).Width;
            var desired = (int)Math.Ceiling(segments.Sum(Measure) + countWidth + 26);
            var chipWidth = Math.Min(width, Math.Clamp(desired, 90, 320));
            if (x > 0 && x + chipWidth > width) { row++; x = 0; }
            if (row >= rows) break;
            var pieces = new List<HotBubblePiece>(); var remaining = Math.Max(1f, chipWidth - countWidth - 26);
            var ellipsis = OverlayTextRenderer.Measure(g, "…", font).Width;
            foreach (var part in segments)
            {
                var measured = Measure(part);
                if (measured <= remaining) { pieces.Add(new(part.Text, part.Emote, measured)); remaining -= measured; continue; }
                if (part.Emote is not null) { if (remaining >= ellipsis) pieces.Add(new("…", null, ellipsis)); break; }
                var text = part.Text; var elements = StringInfo.ParseCombiningCharacters(text); var low = 0; var high = elements.Length;
                while (low < high)
                {
                    var mid = (low + high + 1) / 2; var end = mid == elements.Length ? text.Length : elements[mid];
                    if (OverlayTextRenderer.Measure(g, text[..end] + "…", font).Width <= remaining) low = mid; else high = mid - 1;
                }
                var length = low == elements.Length ? text.Length : elements[low];
                if (remaining >= ellipsis) pieces.Add(new(text[..length] + "…", null, remaining));
                break;
            }
            result.Add(new(phrase, new(x, row * rowHeight, chipWidth, rowHeight - 4), pieces, countWidth));
            x += chipWidth + 6;
        }
        return result;
    }
}

internal sealed class HotBubbleMotion
{
    private double _x, _y, _alpha, _tx, _ty, _ta, _at, _seconds;
    internal bool Leaving { get; private set; }
    internal bool Running(double now) => now < _at + _seconds;
    internal (float X, float Y, float Alpha) Sample(double now)
    {
        var t = _seconds <= 0 ? 1 : Math.Clamp((now - _at) / _seconds, 0, 1); t = 1 - Math.Pow(1 - t, 3);
        return ((float)(_x + (_tx - _x) * t), (float)(_y + (_ty - _y) * t), (float)(_alpha + (_ta - _alpha) * t));
    }
    internal void Start(float x, float y, double now, double seconds, bool enter)
    { _x = _tx = x; _y = y + (enter ? 10 : 0); _ty = y; _alpha = enter ? 0 : 1; _ta = 1; _at = now; _seconds = enter ? seconds : 0; }
    internal void Place(float x, float y, double now, double seconds)
    { if (!Leaving && _tx == x && _ty == y) return; Retarget(x, y, 1, now, seconds); Leaving = false; }
    internal void Leave(double now, double seconds)
    { if (Leaving) return; var v = Sample(now); Retarget(v.X, v.Y + 10, 0, now, seconds); Leaving = true; }
    private void Retarget(float x, float y, double alpha, double now, double seconds)
    { var v = Sample(now); _x = v.X; _y = v.Y; _alpha = v.Alpha; _tx = x; _ty = y; _ta = alpha; _at = now; _seconds = seconds; }
}
