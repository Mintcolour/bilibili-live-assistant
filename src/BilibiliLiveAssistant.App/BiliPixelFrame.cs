using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace BilibiliLiveAssistant.App;

// Owns a tightly packed immutable BGRA8, premultiplied-alpha frame. Consumers
// copy pixels into their own native surfaces; no pinned managed buffer escapes.
internal sealed class BiliPixelFrame
{
    private readonly byte[] _pixels;
    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public long Bytes => _pixels.LongLength;
    public ReadOnlyMemory<byte> Pixels => _pixels;
    internal BiliPixelFrame(int width, int height, byte[] ownedPixels)
    {
        if (width <= 0 || height <= 0 || ownedPixels.Length != checked(width * height * 4)) throw new InvalidDataException("无效的图片像素布局");
        Width = width; Height = height; _pixels = ownedPixels;
    }
    internal SoftwareBitmap CreateSoftwareBitmap() => SoftwareBitmap.CreateCopyFromBuffer(
        _pixels.AsBuffer(), BitmapPixelFormat.Bgra8, Width, Height, BitmapAlphaMode.Premultiplied);

    internal Bitmap CreateDrawingBitmap()
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        try
        {
            var data = bitmap.LockBits(new(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { for (var row = 0; row < Height; row++) Marshal.Copy(_pixels, row * Stride, data.Scan0 + row * data.Stride, Stride); }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
    internal static BiliPixelFrame FromDrawingBitmap(Bitmap bitmap)
    {
        var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        var stride = checked(bitmap.Width * 4);
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try { for (var row = 0; row < bitmap.Height; row++) Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * stride, stride); }
        finally { bitmap.UnlockBits(data); }
        return new(bitmap.Width, bitmap.Height, pixels);
    }
    internal BiliPixelFrame AtWidth(int requestedWidth)
    {
        if (requestedWidth >= Width) return this;
        var width = Math.Clamp(requestedWidth, 1, Width);
        var height = Math.Max(1, (int)Math.Ceiling((double)Height * width / Width));
        using var original = CreateDrawingBitmap();
        using var resized = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(original, new Rectangle(0, 0, width, height));
        }
        return FromDrawingBitmap(resized);
    }
}
