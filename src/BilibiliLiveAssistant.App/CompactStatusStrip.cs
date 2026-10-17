using System.Drawing;
using System.Drawing.Drawing2D;

namespace BilibiliLiveAssistant.App;

// Two cached sprites; animation only copies pixels inside the rank viewport.
internal sealed class CompactStatusStrip(Bitmap prefix, Bitmap? members, int width) : IDisposable
{
    internal const double Speed = 40;
    internal Bitmap Prefix { get; } = prefix;
    internal Bitmap? Members { get; } = members;
    internal int Width { get; } = width;
    internal int Height { get; } = Math.Max(prefix.Height, members?.Height ?? 0);
    internal int RankLeft { get; } = Math.Min(prefix.Width, Math.Max(0, width - 48));
    internal int RankWidth => Width - RankLeft;
    internal bool Moving => Members is not null && RankWidth > 0;
    internal double Period => Math.Max(Members?.Width ?? 0, RankWidth) + 24d;
    internal long Bytes => ((long)Prefix.Width * Prefix.Height + (long)(Members?.Width ?? 0) * (Members?.Height ?? 0)) * 4;

    internal void Draw(Graphics graphics, Point location, double seconds)
    {
        var state = graphics.Save();
        try
        {
            var prefixClip = new Rectangle(location.X, location.Y, RankLeft, Height);
            graphics.SetClip(prefixClip, CombineMode.Intersect);
            OverlayBitmapBlitter.Draw(graphics, Prefix, location.X, location.Y + (Height - Prefix.Height) / 2f, prefixClip);
        }
        finally { graphics.Restore(state); }
        if (Members is not { } image || RankWidth <= 0) return;
        var clip = new Rectangle(location.X + RankLeft, location.Y, RankWidth, Height);
        state = graphics.Save();
        try
        {
            graphics.SetClip(clip, CombineMode.Intersect);
            var x = clip.Left - (float)(Math.Max(0, seconds) * Speed % Period);
            var y = location.Y + (Height - image.Height) / 2f;
            OverlayBitmapBlitter.Draw(graphics, image, x, y, clip);
            OverlayBitmapBlitter.Draw(graphics, image, x + (float)Period, y, clip);
        }
        finally { graphics.Restore(state); }
    }

    public void Dispose() { Prefix.Dispose(); Members?.Dispose(); }
}
