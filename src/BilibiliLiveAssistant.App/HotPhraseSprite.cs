using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using BilibiliLiveAssistant.Core;
using Microsoft.UI.Dispatching;

namespace BilibiliLiveAssistant.App;

// Only admitted chips own bitmaps and cancelable image consumers. No idle frame loop.
internal sealed class HotPhraseSprite(BiliImageCache cache, Action invalidate) : IDisposable
{
    private sealed class Chip(HotBubbleLayout layout) : IDisposable
    {
        public HotBubbleLayout Layout = layout;
        public readonly HotBubbleMotion Motion = new();
        public Bitmap? Image;
        public CancellationTokenSource Cancellation = new();
        public string ResourceKey = "";
        public double RetryAt;
        public bool Loading, Loaded, Top;
        public void Dispose() { Cancellation.Cancel(); Cancellation.Dispose(); Image?.Dispose(); Image = null; }
    }
    private readonly Dictionary<string, Chip> _chips = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private IReadOnlyList<HotPhrase>? _source;
    private HotPhraseSettings? _style;
    private string _family = "";
    private Size _size;
    private int _maxRows;
    private bool _ready;
    internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    internal bool Moving => _chips.Values.Any(c => c.Motion.Running(Now));
    internal bool HasContent => _chips.Count > 0;
    internal long Bytes => _chips.Values.Sum(c => c.Image is { } b ? (long)b.Width * b.Height * 4 : 0);
    internal void Update(IReadOnlyList<HotPhrase> source, HotPhraseSettings style, Size size, int maxRows, string family)
    {
        if (ReferenceEquals(_source, source) && _style == style && _size == size && _family == family && _maxRows == maxRows) return;
        var restyle = _style != style || _family != family; var animate = style.AnimationEnabled && _ready && !restyle;
        _source = source; _style = style; _size = size; _family = family; _maxRows = maxRows;
        var layouts = HotBubbleLayouts.Create(source, style, size.Width, size.Height, maxRows, family);
        var keep = new HashSet<string>(); var now = Now; var seconds = animate ? style.AnimationDurationMs / 1000d : 0;
        foreach (var layout in layouts)
        {
            var key = HotBubbleLayouts.Key(layout.Phrase); keep.Add(key);
            if (!_chips.TryGetValue(key, out var chip))
            { chip = new(layout); chip.Motion.Start(layout.Bounds.X, layout.Bounds.Y, now, seconds, animate); _chips.Add(key, chip); }
            else if (!animate) chip.Motion.Start(layout.Bounds.X, layout.Bounds.Y, now, 0, false);
            else chip.Motion.Place(layout.Bounds.X, layout.Bounds.Y, now, seconds);
            var resources = string.Join('|', layout.Pieces.Where(p => p.Emote is not null).Select(p => p.Emote!.Url + ":" + BiliImageCache.SizeTier(Math.Max(style.EmoteSize, p.Width - 3))));
            if (chip.ResourceKey != resources)
            {
                chip.Cancellation.Cancel(); chip.Cancellation.Dispose(); chip.Cancellation = new();
                chip.ResourceKey = resources; chip.Loading = chip.Loaded = false; chip.RetryAt = 0;
            }
            var rerender = restyle || chip.Image is null || chip.Layout.Phrase.Count != layout.Phrase.Count || chip.Layout.Bounds.Size != layout.Bounds.Size
                || (_source?.Count > 0 && HotBubbleLayouts.Key(_source[0]) == key) != chip.Top;
            chip.Top = source.Count > 0 && HotBubbleLayouts.Key(source[0]) == key; chip.Layout = layout;
            if (rerender) Paint(chip);
            if (!chip.Loaded && !chip.Loading && now >= chip.RetryAt) _ = LoadAsync(key, chip);
        }
        foreach (var pair in _chips.Where(p => !keep.Contains(p.Key)).ToArray())
        {
            if (animate && !pair.Value.Motion.Leaving) pair.Value.Motion.Leave(now, seconds);
            else if (!animate || !pair.Value.Motion.Running(now)) Remove(pair.Key);
        }
        foreach (var key in _chips.Where(p => p.Value.Motion.Leaving).Skip(5).Select(p => p.Key).ToArray()) Remove(key);
        _ready = true;
    }
    private async Task LoadAsync(string key, Chip chip)
    {
        chip.Loading = true; var token = chip.Cancellation.Token;
        try
        {
            var size = _style!.EmoteSize;
            foreach (var piece in chip.Layout.Pieces.Where(p => p.Emote is not null).DistinctBy(p => p.Emote!.Url))
            {
                var emote = piece.Emote!;
                if (!emote.Url.StartsWith("local:", StringComparison.Ordinal))
                    await cache.GetStaticForViewAsync(emote.Url, token, BiliImageCache.SizeTier(Math.Max(size, piece.Width - 3))).ConfigureAwait(false);
            }
            _dispatcher.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || !_chips.TryGetValue(key, out var current) || !ReferenceEquals(chip, current)) return;
                chip.Loaded = true; chip.Loading = false;
                try { Paint(chip); invalidate(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dispatcher.TryEnqueue(() => { if (!token.IsCancellationRequested && _chips.TryGetValue(key, out var current) && ReferenceEquals(current, chip)) { chip.Loading = false; chip.RetryAt = Now + 30; } });
        }
    }
    private void Paint(Chip chip)
    {
        var s = _style!; var layout = chip.Layout; chip.Image?.Dispose();
        var bitmap = chip.Image = new Bitmap(layout.Bounds.Width, layout.Bounds.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shadow = new SolidBrush(Color.FromArgb(65, 0, 0, 0));
        using var shade = OverlayTextRenderer.RoundedRectangle(new RectangleF(2, 3, bitmap.Width - 4, bitmap.Height - 4), 9); g.FillPath(shadow, shade);
        using var fill = new SolidBrush(Color.FromArgb((int)(255 * s.BackgroundOpacity), 35, 40, 52));
        using var path = OverlayTextRenderer.RoundedRectangle(new RectangleF(1, 1, bitmap.Width - 4, bitmap.Height - 5), 9); g.FillPath(fill, path);
        using var pen = new Pen(chip.Top ? Color.FromArgb(205, 224, 181, 100) : Color.FromArgb(120, 122, 139, 166)); g.DrawPath(pen, path);
        using var font = OverlayTextRenderer.CreateFont(_family, s.FontSize); var x = 10f;
        foreach (var piece in layout.Pieces)
        {
            if (piece.Emote is { Url: "local:hot-smile" })
            {
                var y = (bitmap.Height - s.EmoteSize) / 2f; var size = s.EmoteSize;
                using var face = new SolidBrush(Color.FromArgb(255, 208, 72)); g.FillEllipse(face, x, y, size, size);
                g.FillEllipse(Brushes.Black, x + size * .27f, y + size * .28f, 2, 3); g.FillEllipse(Brushes.Black, x + size * .66f, y + size * .28f, 2, 3);
                using var smile = new Pen(Color.Black, 1.5f); g.DrawArc(smile, x + size * .23f, y + size * .36f, size * .54f, size * .38f, 0, 180);
            }
            else if (piece.Emote is { } emote && cache.TryGetStaticFrame(emote.Url, BiliImageCache.SizeTier(Math.Max(s.EmoteSize, piece.Width - 3))) is { } frame)
            { using var image = frame.CreateDrawingBitmap(); g.DrawImage(image, new RectangleF(x, (bitmap.Height - s.EmoteSize) / 2f, piece.Width - 3, s.EmoteSize)); }
            else
            {
                var saved = g.Save(); g.SetClip(new RectangleF(x, 2, piece.Width, bitmap.Height - 4));
                OverlayTextRenderer.Draw(g, piece.Text, font, new(x, (bitmap.Height - font.GetHeight(g)) / 2), Color.FromArgb(241, 243, 248), false); g.Restore(saved);
            }
            x += piece.Width;
        }
        OverlayTextRenderer.Draw(g, "×" + layout.Phrase.Count, font, new(bitmap.Width - layout.CountWidth - 5, (bitmap.Height - font.GetHeight(g)) / 2), Color.FromArgb(233, 207, 146), false);
    }
    internal void Draw(Graphics g, Point location, double opacity = 1)
    {
        var now = Now;
        var removed = false;
        foreach (var pair in _chips.ToArray())
        {
            var chip = pair.Value;
            if (chip.Motion.Leaving && !chip.Motion.Running(now)) { Remove(pair.Key); removed = true; continue; }
            if (chip.Image is not { } image) continue;
            var p = chip.Motion.Sample(now);
            using var attributes = new ImageAttributes(); attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)(p.Alpha * opacity) });
            g.DrawImage(image, new Rectangle(location.X + (int)p.X, location.Y + (int)p.Y, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }
        if (removed) invalidate();
    }
    private void Remove(string key) { if (_chips.Remove(key, out var chip)) chip.Dispose(); }
    public void Dispose() { foreach (var chip in _chips.Values) chip.Dispose(); _chips.Clear(); _source = null; _ready = false; }
}
