using System.Drawing;

namespace BilibiliLiveAssistant.App;

internal static class OverlayBitmapBlitter
{
    // GDI+ may prepare/resample the entire source before applying its destination
    // clip. Crop the source too, preserving the exact 1:1 fractional translation.
    internal static void Draw(Graphics graphics, Bitmap image, float x, float y, Rectangle clip)
    {
        // Two source pixels of bleed keep bilinear edge sampling identical inside
        // the original dirty clip. Graphics' existing region remains authoritative.
        var left = (int)Math.Clamp(Math.Floor(clip.Left - (double)x) - 2, 0, image.Width);
        var top = (int)Math.Clamp(Math.Floor(clip.Top - (double)y) - 2, 0, image.Height);
        var right = (int)Math.Clamp(Math.Ceiling(clip.Right - (double)x) + 2, 0, image.Width);
        var bottom = (int)Math.Clamp(Math.Ceiling(clip.Bottom - (double)y) + 2, 0, image.Height);
        var width = right - left; var height = bottom - top;
        if (width <= 0 || height <= 0) return;
        PerformanceCounters.Blit((long)image.Width * image.Height, (long)width * height);
        graphics.DrawImage(image, new RectangleF(x + left, y + top, width, height),
            new RectangleF(left, top, width, height), GraphicsUnit.Pixel);
    }
}
