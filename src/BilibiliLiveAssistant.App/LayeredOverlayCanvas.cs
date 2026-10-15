using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BilibiliLiveAssistant.App;

/// <summary>Reusable premultiplied-alpha DIB. Native presentation never reconstructs text or image assets.</summary>
internal sealed class LayeredOverlayCanvas : IDisposable
{
    private nint _dc;
    private nint _dib;
    private nint _originalBitmap;
    private nint _destination;
    private nint _source;
    private nint _size;
    private nint _blend;
    private nint _dirty;
    private Bitmap? _bitmap;
    private bool _presented;
    private bool _indirect = true;
    private long _trackedBytes;
    public Graphics Graphics { get; private set; } = null!;
    public int Width { get; }
    public int Height { get; }

    public LayeredOverlayCanvas(int width, int height)
    {
        Width = width; Height = height;
        try
        {
            _dc = CreateCompatibleDC(0);
            if (_dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new BitmapInfo { Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32,
                SizeImage = checked((uint)(width * height * 4)),
            } };
            _dib = CreateDIBSection(_dc, ref info, 0, out var pixels, 0, 0);
            if (_dib == 0 || pixels == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _originalBitmap = SelectObject(_dc, _dib);
            if (_originalBitmap == 0 || _originalBitmap == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, pixels);
            Graphics = Graphics.FromImage(_bitmap);
            Graphics.Clear(Color.Transparent);
            _destination = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint>());
            _source = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint>());
            _size = Marshal.AllocHGlobal(Marshal.SizeOf<NativeSize>());
            _blend = Marshal.AllocHGlobal(Marshal.SizeOf<BlendFunction>());
            _dirty = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
            Marshal.StructureToPtr(new NativePoint(), _source, false);
            Marshal.StructureToPtr(new NativeSize { Width = width, Height = height }, _size, false);
            _trackedBytes = (long)width * height * 4;
            PerformanceCounters.CanvasBytes += _trackedBytes; PerformanceCounters.Canvases++;
        }
        catch { Dispose(); throw; }
    }

    public void Present(nint window, Point location, double opacity, Rectangle dirty)
    {
        Graphics.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        var destination = new NativePoint { X = location.X, Y = location.Y };
        var source = new NativePoint();
        var size = new NativeSize { Width = Width, Height = Height };
        var blend = new BlendFunction { SourceConstantAlpha = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255), AlphaFormat = 1 };
        if (!_presented) dirty = new Rectangle(0, 0, Width, Height);
        Marshal.StructureToPtr(destination, _destination, false);
        Marshal.StructureToPtr(blend, _blend, false);
        Marshal.StructureToPtr(new NativeRect { Left = dirty.Left, Top = dirty.Top, Right = dirty.Right, Bottom = dirty.Bottom }, _dirty, false);
        var update = new UpdateInfo
        {
            Size = (uint)Marshal.SizeOf<UpdateInfo>(), DestinationPoint = _destination, WindowSize = _size,
            SourceDc = _dc, SourcePoint = _source, Blend = _blend, Flags = 2, DirtyRect = _dirty,
        };
        if (_indirect && UpdateLayeredWindowIndirect(window, ref update)) { _presented = true; return; }
        _indirect = false;
        if (!UpdateLayeredWindow(window, 0, ref destination, ref size, _dc, ref source, 0, ref blend, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _presented = true;
    }

    public void Dispose()
    {
        if (_trackedBytes > 0)
        {
            PerformanceCounters.CanvasBytes -= _trackedBytes; PerformanceCounters.Canvases--; _trackedBytes = 0;
        }
        Graphics?.Dispose(); _bitmap?.Dispose(); _bitmap = null;
        if (_dc != 0 && _originalBitmap != 0 && _originalBitmap != new nint(-1)) SelectObject(_dc, _originalBitmap);
        if (_dib != 0) { DeleteObject(_dib); _dib = 0; }
        if (_dc != 0) { DeleteDC(_dc); _dc = 0; }
        foreach (var pointer in new[] { _destination, _source, _size, _blend, _dirty }) if (pointer != 0) Marshal.FreeHGlobal(pointer);
        _destination = _source = _size = _blend = _dirty = 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct UpdateInfo
    {
        public uint Size;
        public nint DestinationDc, DestinationPoint, WindowSize, SourceDc, SourcePoint;
        public uint ColorKey;
        public nint Blend;
        public uint Flags;
        public nint DirtyRect;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint bitmap);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateLayeredWindowIndirect(nint window, ref UpdateInfo info);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, ref NativePoint destination, ref NativeSize size,
        nint sourceDc, ref NativePoint source, uint key, ref BlendFunction blend, uint flags);
}
