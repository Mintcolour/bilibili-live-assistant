using System.Drawing;
using System.Drawing.Imaging;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

// Repaint text only when the snapshot or display geometry changes, never per animation frame.
internal sealed class HotPhraseSprite : IDisposable
{
    private IReadOnlyList<HotPhrase>? _source;
    private Bitmap? _image;
    private int _width, _height, _fontSize;
    private string? _fontFamily;
    public static int Height(IReadOnlyList<HotPhrase> items, int availableHeight, int fontSize)
    { var row = Math.Clamp(fontSize, 14, 20) + 10; return Math.Min(items.Count, Math.Max(0, availableHeight / row)) * row; }
    public void Draw(Graphics g, IReadOnlyList<HotPhrase> items, Rectangle bounds, string fontFamily, int fontSize)
    {
        fontSize = Math.Clamp(fontSize, 14, 20);
        if (bounds.Width <= 0 || bounds.Height <= 0 || items.Count == 0) { Dispose(); return; }
        if (!ReferenceEquals(items, _source) || _width != bounds.Width || _height != bounds.Height || _fontSize != fontSize || _fontFamily != fontFamily)
        {
            Dispose(); _source = items; _width = bounds.Width; _height = bounds.Height; _fontSize = fontSize; _fontFamily = fontFamily;
            _image = new(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using var target = Graphics.FromImage(_image);
            using var font = OverlayTextRenderer.CreateFont(fontFamily, fontSize);
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            var row = fontSize + 10;
            for (var i = 0; i < items.Count && (i + 1) * row <= bounds.Height; i++)
            {
                using var background = new SolidBrush(i < 3 ? Color.FromArgb(225, 250, 233, 170) : Color.FromArgb(225, 235, 237, 242));
                using var ink = new SolidBrush(Color.FromArgb(35, 39, 49));
                target.FillRectangle(background, 0, i * row, bounds.Width, row - 3);
                var count = $"×{items[i].Count}"; var countWidth = (int)Math.Ceiling(target.MeasureString(count, font).Width) + 8;
                target.DrawString(items[i].Text, font, ink, new RectangleF(8, i * row + 2, Math.Max(1, bounds.Width - countWidth - 12), row - 3), format);
                target.DrawString(count, font, ink, new PointF(bounds.Width - countWidth, i * row + 2));
            }
        }
        g.DrawImageUnscaled(_image!, bounds.Location);
    }
    public void Dispose() { _image?.Dispose(); _image = null; _source = null; }
}
