using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using BilibiliLiveAssistant.Core;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

// Shared layout editing for independent, non-activating overlay windows.
public abstract class OverlaySurface : Forms.Form
{
    private const int RenderMessage = 0x8000 + 0x512;
    protected readonly LiveAssistantViewModel ViewModel;
    protected OverlayRegionSettings RegionSettings { get; private set; }
    protected OverlayRegionKind Kind { get; }
    private bool _clickThrough;
    private LayeredOverlayCanvas? _canvas;
    private bool _presentationErrorReported;
    private bool _renderQueued;
    private Rectangle _pendingDirty;
    private readonly List<Rectangle> _dirtyRects = new(32);
    private readonly Rectangle[] _frameDirtyRects = new Rectangle[32];
    private bool _inAnimationFrame;
    protected int ContentTop => ViewModel.Settings.Display.LayoutLocked ? 0 : 28;

    protected OverlaySurface(LiveAssistantViewModel vm, OverlayRegionKind kind)
    {
        ViewModel = vm;
        Kind = kind;
        RegionSettings = OverlayRegions.Get(vm.Settings.Display, kind);
        Text = OverlayRegions.Title(kind);
        FormBorderStyle = Forms.FormBorderStyle.None;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        // Color-key transparency contaminates antialiased glyph edges with magenta.
        // Keep a persistent alpha canvas and present only when the content changes.
        DoubleBuffered = false;
        BackColor = Color.Black;
        TransparencyKey = Color.Empty;
        MinimumSize = new Size(160, 80);
        vm.PropertyChanged += OnSettingsChanged;
        ApplySurfaceSettings();
    }

    protected override bool ShowWithoutActivation => true;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var value = base.CreateParams;
            value.ExStyle |= 0x00000080 | 0x08000000 | 0x00080000;
            if (_clickThrough) value.ExStyle |= 0x00000020;
            return value;
        }
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        if (!IsHandleCreated) return;
        var style = GetWindowLongPtr(Handle, -20).ToInt64();
        SetWindowLongPtr(Handle, -20, new nint(enabled ? style | 0x20 : style & ~0x20));
        SetWindowPos(Handle, new nint(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LiveAssistantViewModel.Settings) || IsDisposed) return;
        var previous = RegionSettings;
        ApplySurfaceSettings();
        OnRegionSettingsChanged(previous);
        RequestRender();
    }

    protected virtual void OnRegionSettingsChanged(OverlayRegionSettings previous) { }

    private void ApplySurfaceSettings()
    {
        RegionSettings = OverlayRegions.Get(ViewModel.Settings.Display, Kind);
        // Region opacity is applied by the alpha presenter, never via color-key attributes.
        SetClickThrough(ViewModel.Settings.Display.LayoutLocked);
        var bounds = OverlayLayout.Bounds(ViewModel.Settings.Display, Kind, RegionSettings);
        if (Bounds != bounds) Bounds = bounds;
    }

    protected override void OnPaintBackground(Forms.PaintEventArgs e) { }

    // UpdateLayeredWindow windows need an explicit first submission before Windows
    // can display them. They do not use the normal WM_PAINT redirection path.
    protected void ShowSurface()
    {
        Show();
        RequestRender();
    }

    protected void RequestRender() => RequestRender(ClientRectangle);

    protected void RequestRender(Rectangle dirty)
    {
        if (IsDisposed || Disposing || !IsHandleCreated || !Visible) return;
        dirty = Rectangle.Intersect(ClientRectangle, dirty);
        if (dirty.IsEmpty) return;
        _pendingDirty = _pendingDirty.IsEmpty ? dirty : Rectangle.Union(_pendingDirty, dirty);
        AddDirtyRectangle(dirty);
        if (_inAnimationFrame) return;
        if (_renderQueued) return;
        _renderQueued = true;
        // One queued message per burst/frame, without an idle polling timer.
        if (PostMessage(Handle, RenderMessage, 0, 0)) return;
        _renderQueued = false;
        ReportPresentationError(new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private void AddDirtyRectangle(Rectangle dirty)
    {
        // Keep separated tracks separated; their bounding box can include most of an empty screen.
        for (var i = _dirtyRects.Count - 1; i >= 0; i--)
        {
            if (!_dirtyRects[i].IntersectsWith(dirty)) continue;
            dirty = Rectangle.Union(dirty, _dirtyRects[i]);
            _dirtyRects.RemoveAt(i);
        }
        if (_dirtyRects.Count == _frameDirtyRects.Length) { _dirtyRects.Clear(); dirty = _pendingDirty; }
        _dirtyRects.Add(dirty);
    }

    protected void BeginAnimationFrame() => _inAnimationFrame = true;
    protected void EndAnimationFrame()
    {
        _inAnimationFrame = false;
        // Submit this frame directly; do not queue a second animation/render hop.
        RenderPending();
    }

    protected override void OnInvalidated(Forms.InvalidateEventArgs e)
    {
        base.OnInvalidated(e);
        RequestRender(e.InvalidRect);
    }

    protected override void OnPaint(Forms.PaintEventArgs e) => RequestRender(e.ClipRectangle);

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) RequestRender();
        else
        {
            _pendingDirty = Rectangle.Empty;
            _dirtyRects.Clear();
            _renderQueued = false;
            _canvas?.Dispose(); _canvas = null;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RequestRender();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _pendingDirty = Rectangle.Empty;
        _dirtyRects.Clear();
        _renderQueued = false;
        _canvas?.Dispose(); _canvas = null;
        base.OnHandleDestroyed(e);
    }

    private void RenderPending()
    {
        _renderQueued = false;
        var dirty = _pendingDirty;
        var dirtyCount = _dirtyRects.Count;
        _dirtyRects.CopyTo(_frameDirtyRects);
        _dirtyRects.Clear();
        _pendingDirty = Rectangle.Empty;
        if (IsDisposed || Disposing || !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0 || !Visible) return;
        try
        {
            dirty = Rectangle.Intersect(ClientRectangle, dirty);
            if (dirty.IsEmpty) return;
            if (_canvas is null || _canvas.Width != ClientSize.Width || _canvas.Height != ClientSize.Height)
            {
                _canvas?.Dispose(); _canvas = null;
                _canvas = new LayeredOverlayCanvas(ClientSize.Width, ClientSize.Height);
                dirty = ClientRectangle;
                dirtyCount = 1; _frameDirtyRects[0] = dirty;
            }
            var graphics = _canvas.Graphics;
            var state = graphics.Save();
            try
            {
                if (dirtyCount <= 1) graphics.SetClip(dirty);
                else
                {
                    using var dirtyRegion = new Region(); dirtyRegion.MakeEmpty();
                    for (var i = 0; i < dirtyCount; i++) dirtyRegion.Union(_frameDirtyRects[i]);
                    graphics.SetClip(dirtyRegion, CombineMode.Replace);
                }
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.CompositingMode = CompositingMode.SourceCopy;
                using (var background = new SolidBrush(ContentTop == 0 ? Color.Transparent : Color.FromArgb(225, 32, 36, 42)))
                    graphics.FillRectangle(background, dirty);
                graphics.CompositingMode = CompositingMode.SourceOver;
                DrawContent(graphics, dirty);
                DrawEditorChrome(graphics);
            }
            finally { graphics.Restore(state); }
            _canvas.Present(Handle, Location, RegionSettings.Opacity, dirty);
            _presentationErrorReported = false;
        }
        catch (ExternalException ex)
        {
            ReportPresentationError(ex);
        }
    }

    private void ReportPresentationError(ExternalException ex)
    {
        if (_presentationErrorReported) return;
        _presentationErrorReported = true;
        var code = ex is Win32Exception native ? native.NativeErrorCode : ex.ErrorCode;
        ViewModel.ReportSettingsError($"{Text}悬浮窗口绘制失败：{ex.Message}（错误码 {code}）");
    }

    private void DrawEditorChrome(Graphics graphics)
    {
        if (ContentTop != 0)
        {
            using var header = new SolidBrush(Color.FromArgb(50, 58, 70));
            graphics.FillRectangle(header, 0, 0, ClientSize.Width, ContentTop);
            // GDI text does not write alpha; use the same filled glyph paths as the message renderer.
            using var font = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
            OverlayTextRenderer.Draw(graphics, $"{Text} · 拖动区域，边缘调整大小", font, new PointF(8, 5), Color.White, false);
            using var pen = new Pen(Color.FromArgb(100, 160, 210));
            graphics.DrawRectangle(pen, 0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
        }
    }

    protected abstract void DrawContent(Graphics graphics, Rectangle clip);

    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == RenderMessage)
        {
            RenderPending();
            m.Result = 0;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg != 0x0084 || ViewModel.Settings.Display.LayoutLocked) return;
        var packed = m.LParam.ToInt64();
        var p = PointToClient(new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff))));
        var left = p.X < 8; var right = p.X >= ClientSize.Width - 8;
        var top = p.Y < 8; var bottom = p.Y >= ClientSize.Height - 8;
        m.Result = new nint(top ? left ? 13 : right ? 14 : 12 : bottom ? left ? 16 : right ? 17 : 15 : left ? 10 : right ? 11 : 2);
    }

    protected override async void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (ViewModel.Settings.Display.LayoutLocked || IsDisposed) return;
        try
        {
            var current = OverlayRegions.Get(ViewModel.Settings.Display, Kind) with { X = Left, Y = Top, Width = Width, Height = Height };
            await ViewModel.SaveAsync(ViewModel.Settings with { Display = OverlayRegions.With(ViewModel.Settings.Display, Kind, current) });
        }
        catch (Exception ex) { ViewModel.ReportSettingsError($"保存区域位置失败：{ex.Message}"); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { ViewModel.PropertyChanged -= OnSettingsChanged; _canvas?.Dispose(); _canvas = null; }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
