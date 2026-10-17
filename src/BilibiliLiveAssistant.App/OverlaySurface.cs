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
    protected object DiagnosticAppearance => new { mode = RegionSettings.Mode.ToString(), width = ClientSize.Width,
        height = ClientSize.Height, RegionSettings.FontSize, RegionSettings.EmoteSize,
        RegionSettings.ContentScale, RegionSettings.Opacity, RegionSettings.BackgroundOpacity, RegionSettings.Shadow };
    private bool _clickThrough;
    private LayeredOverlayCanvas? _canvas;
    private bool _presentationErrorReported;
    private bool _renderQueued;
    private Rectangle _pendingDirty;
    private readonly List<Rectangle> _dirtyRects = new(32);
    private readonly Rectangle[] _frameDirtyRects = new Rectangle[32];
    private bool _inAnimationFrame;
    private Forms.ContextMenuStrip? _editorMenu;
    private OverlayQuickEditor? _quickEditor;
    private OverlayRegionSettings? _previewRegion;
    private OverlayRegionSettings? _inlineRegion;
    private bool? _inlineEnabled;
    private bool _inlineDirty, _wasEditing, _sampleDirty = true;
    private Task? _inlineSaveTask;
    private int _slider = -1;
    private readonly Forms.Timer _inlinePreviewTimer = new() { Interval = 60 };
    private readonly OverlayEditorPreview _samplePreview = new();
    private OverlayFrameClock? _editorClock;
    private readonly Action<Rectangle> _invalidateSample;
    private OverlayRegionSettings RawRegion => _previewRegion ?? _inlineRegion ?? OverlayRegions.Get(ViewModel.Settings.Display, Kind);
    private bool ContentEnabled => _inlineEnabled ?? OverlayRegions.Enabled(ViewModel.Settings.Display, Kind);
    protected bool Editing => ViewModel.OverlayInteractive || !ViewModel.Settings.Display.LayoutLocked;
    protected int ContentTop => Editing ? OverlayInlineControls.ContentTop(ClientSize.Width) : 0;
    private int ActionTop => ClientSize.Width < 360 ? 32 : 8;
    private Rectangle DoneBounds => new(Math.Max(4, ClientSize.Width - 48), ActionTop, 40, 22);
    private Rectangle ModeBounds => new(Math.Max(4, DoneBounds.Left - (ClientSize.Width < 280 ? 52 : 96) - 4), ActionTop, ClientSize.Width < 280 ? 52 : 96, 22);
    private Rectangle VisibilityBounds => new(Math.Max(4, ModeBounds.Left - 52), ActionTop, 48, 22);

    protected OverlaySurface(LiveAssistantViewModel vm, OverlayRegionKind kind)
    {
        ViewModel = vm;
        _invalidateSample = RequestRender;
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
        _wasEditing = Editing;
        _inlinePreviewTimer.Tick += (_, _) => { _inlinePreviewTimer.Stop(); ApplyInlinePreview(); };
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
        if (IsDisposed || e.PropertyName is not (nameof(LiveAssistantViewModel.Settings) or nameof(LiveAssistantViewModel.OverlayInteractive))) return;
        var previous = RegionSettings;
        ApplySurfaceSettings();
        if (_wasEditing != Editing)
        {
            _wasEditing = Editing;
            _slider = -1; Capture = false;
            OnInteractionChanged();
            if (!Editing) { _editorClock?.Stop(OnEditorFrame); _editorMenu?.Close(); _quickEditor?.Close(); _ = SaveInlineAsync(); _samplePreview.Dispose(); }
        }
        _sampleDirty = true;
        OnRegionSettingsChanged(previous);
        RequestRender();
    }

    protected virtual void OnRegionSettingsChanged(OverlayRegionSettings previous) { }
    protected virtual void OnInteractionChanged() { }

    private void ApplySurfaceSettings()
    {
        RegionSettings = OverlayRegions.ForRendering(RawRegion);
        // Region opacity is applied by the alpha presenter, never via color-key attributes.
        SetClickThrough(ViewModel.Settings.Display.LayoutLocked && !ViewModel.OverlayInteractive);
        var bounds = OverlayLayout.Bounds(ViewModel.Settings.Display, Kind, RegionSettings);
        // Very short saved regions still need room for the editing controls and
        // a sample. This temporary expansion is not written by slider changes.
        var minimum = new Size(160, Editing ? OverlayInlineControls.ContentTop(bounds.Width) + 72 : 80);
        if (MinimumSize != minimum) MinimumSize = minimum;
        if (Editing)
        {
            var screen = Forms.Screen.FromRectangle(bounds).WorkingArea;
            bounds.Height = Math.Min(screen.Height, Math.Max(bounds.Height, minimum.Height));
            bounds.Y = Math.Clamp(bounds.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - bounds.Height));
        }
        if (_previewRegion is null && _inlineRegion is null && Bounds != bounds) Bounds = bounds;
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
            _editorClock?.Stop(OnEditorFrame);
            _inlinePreviewTimer.Stop();
            _samplePreview.Dispose(); _sampleDirty = true;
            _quickEditor?.Close();
            _pendingDirty = Rectangle.Empty;
            _dirtyRects.Clear();
            _renderQueued = false;
            _canvas?.Dispose(); _canvas = null;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _sampleDirty = true;
        RequestRender();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _editorClock?.Stop(OnEditorFrame);
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
        if (_canvas is null && !Editing && !HasLiveContent) return;
        using var timing = PerformanceCounters.Measure(3 + (int)Kind);
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
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                using (var background = new SolidBrush(ContentTop == 0 ? Color.Transparent : Color.FromArgb(24, 32, 36, 42)))
                using (PerformanceCounters.Measure(8))
                {
                    // Keep separated tracks rectangular and crop source pixels
                    // to the current track before asking GDI+ to resample them.
                    for (var i = 0; i < Math.Max(1, dirtyCount); i++)
                    {
                        var area = dirtyCount <= 1 ? dirty : Rectangle.Intersect(ClientRectangle, _frameDirtyRects[i]);
                        if (area.IsEmpty) continue;
                        graphics.SetClip(area, CombineMode.Replace);
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.FillRectangle(background, area);
                        graphics.CompositingMode = CompositingMode.SourceOver;
                        if (Editing) DrawEditorSample(graphics);
                        else DrawContent(graphics, area);
                        DrawEditorChrome(graphics);
                    }
                }
            }
            finally { graphics.Restore(state); }
            // Keep controls readable at low content opacity. Only samples receive
            // content alpha while editing; live mode uses the existing presenter.
            using (PerformanceCounters.Measure(9)) _canvas.Present(Handle, Location, Editing ? 1 : RegionSettings.Opacity, dirty);
            // Present the final clear before releasing the native backing store.
            // Empty, still-visible windows need no persistent full-size canvas.
            if (!Editing && !HasLiveContent) { _canvas.Dispose(); _canvas = null; }
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
            if (graphics.IsVisible(new Rectangle(0, 0, ClientSize.Width, ContentTop)))
            {
                using var header = new SolidBrush(Color.FromArgb(50, 58, 70));
                graphics.FillRectangle(header, 0, 0, ClientSize.Width, ContentTop);
                // GDI text does not write alpha; use the same filled glyph paths as the message renderer.
                using var font = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
                var state = graphics.Save();
                graphics.SetClip(new Rectangle(8, 8, Math.Max(0, ClientSize.Width < 360 ? ClientSize.Width - 16 : VisibilityBounds.Left - 12), 22), CombineMode.Intersect);
                OverlayTextRenderer.Draw(graphics, Text + " · 示例", font, new PointF(8, 11), Color.White, false);
                graphics.Restore(state);
                using var button = new SolidBrush(Color.FromArgb(76, 88, 108));
                graphics.FillRectangle(button, ModeBounds); graphics.FillRectangle(button, DoneBounds); graphics.FillRectangle(button, VisibilityBounds);
                state = graphics.Save(); graphics.SetClip(ModeBounds, CombineMode.Intersect);
                OverlayTextRenderer.Draw(graphics, ClientSize.Width < 280 ? "样式 ▾" : "更多外观 ▾", font, new PointF(ModeBounds.X + 3, ActionTop + 3), Color.White, false);
                graphics.Restore(state);
                OverlayTextRenderer.Draw(graphics, "完成", font, new PointF(DoneBounds.X + 6, ActionTop + 3), Color.White, false);
                OverlayTextRenderer.Draw(graphics, ContentEnabled ? "隐藏" : "显示", font, new PointF(VisibilityBounds.X + 10, ActionTop + 3), Color.White, false);
                OverlayInlineControls.Draw(graphics, ClientSize.Width, RawRegion, Kind);
            }
            using var pen = new Pen(Color.FromArgb(100, 160, 210));
            graphics.DrawRectangle(pen, 0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
            foreach (var p in new[] { new Point(1, 1), new Point(ClientSize.Width - 7, 1), new Point(1, ClientSize.Height - 7), new Point(ClientSize.Width - 7, ClientSize.Height - 7) })
                graphics.DrawRectangle(pen, p.X, p.Y, 5, 5);
        }
    }

    protected abstract void DrawContent(Graphics graphics, Rectangle clip);
    protected abstract bool HasLiveContent { get; }

    private void DrawEditorSample(Graphics graphics)
    {
        if (!ContentEnabled)
        {
            _editorClock?.Stop(OnEditorFrame);
            _samplePreview.Dispose();
            using var font = OverlayTextRenderer.CreateFont("Microsoft YaHei UI", 12);
            OverlayTextRenderer.Draw(graphics, "内容已隐藏", font, new(10, ContentTop + 8), Color.LightGray, false);
            return;
        }
        if (_sampleDirty)
        {
            _samplePreview.Rebuild(Kind, RegionSettings, ViewModel.Settings.Dashboard, ViewModel.Images, ClientSize.Width, ClientSize.Height, ContentTop);
            _sampleDirty = false;
            if (_samplePreview.IsScrolling && Visible && Editing)
                _editorClock?.Start(OnEditorFrame);
            else _editorClock?.Stop(OnEditorFrame);
        }
        var state = graphics.Save();
        graphics.SetClip(new Rectangle(0, ContentTop, ClientSize.Width, Math.Max(0, ClientSize.Height - ContentTop)), CombineMode.Intersect);
        _samplePreview.Draw(graphics, RegionSettings.Opacity); graphics.Restore(state);
    }

    internal void SetEditorClock(OverlayFrameClock clock) => _editorClock = clock;

    private void OnEditorFrame(double elapsed)
    {
        if (IsDisposed || !Visible || !Editing || !ContentEnabled || !_samplePreview.IsScrolling)
        { _editorClock?.Stop(OnEditorFrame); return; }
        BeginAnimationFrame();
        _samplePreview.Advance(elapsed, ClientSize.Width, RegionSettings.ScrollSpeed, _invalidateSample);
        EndAnimationFrame();
    }

    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == RenderMessage)
        {
            RenderPending();
            m.Result = 0;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg != 0x0084 || !Editing) return;
        var packed = m.LParam.ToInt64();
        var p = PointToClient(new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff))));
        var left = p.X < 8; var right = p.X >= ClientSize.Width - 8;
        var top = p.Y < 8; var bottom = p.Y >= ClientSize.Height - 8;
        var control = ModeBounds.Contains(p) || DoneBounds.Contains(p) || VisibilityBounds.Contains(p) || p.Y >= OverlayInlineControls.HeaderHeight(ClientSize.Width);
        m.Result = new nint(top ? left ? 13 : right ? 14 : 12 : bottom ? left ? 16 : right ? 17 : 15 : left ? 10 : right ? 11 : control ? 1 : p.Y < ContentTop ? 2 : 1);
    }

    protected override async void OnMouseClick(Forms.MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!Editing || e.Button != Forms.MouseButtons.Left) return;
        var modes = OverlayRegions.Modes(Kind);
        for (var i = 0; i < modes.Length; i++)
        {
            if (!OverlayInlineControls.Mode(ClientSize.Width, i, modes.Length).Contains(e.Location)) continue;
            if (RegionSettings.Mode != modes[i]) await ChangeModeAsync(modes[i]);
            return;
        }
        if (VisibilityBounds.Contains(e.Location))
        {
            _quickEditor?.Close(); _inlineEnabled = !ContentEnabled; _inlineDirty = true;
            _sampleDirty = true; RequestRender(); await SaveInlineAsync(); return;
        }
        if (DoneBounds.Contains(e.Location)) { await FinishEditingAsync(); return; }
        if (!ModeBounds.Contains(e.Location)) return;
        _editorMenu?.Dispose(); _editorMenu = new();
        foreach (var mode in OverlayRegions.Modes(Kind))
        {
            var item = new Forms.ToolStripMenuItem(OverlayRegions.ModeTitle(mode)) { Checked = RegionSettings.Mode == mode };
            item.Click += async (_, _) =>
            {
                await ChangeModeAsync(mode);
            };
            _editorMenu.Items.Add(item);
        }
        _editorMenu.Items.Add(new Forms.ToolStripSeparator());
        if (Kind == OverlayRegionKind.Interaction)
        {
            var avatars = new Forms.ToolStripMenuItem("加载进房头像与头像框") { Checked = ViewModel.Settings.Dashboard.ShowEntryAvatars };
            avatars.Click += async (_, _) =>
            {
                try
                {
                    var current = ViewModel.Settings;
                    await ViewModel.SaveAsync(current with { Dashboard = current.Dashboard with { ShowEntryAvatars = !current.Dashboard.ShowEntryAvatars } });
                }
                catch (Exception ex) { ViewModel.ReportSettingsError("保存进房头像设置失败：" + ex.Message); }
            };
            _editorMenu.Items.Add(avatars);
        }
        var appearance = new Forms.ToolStripMenuItem("大小、透明度、背景与时间…");
        appearance.Click += async (_, _) => { await SaveInlineAsync(); if (!IsDisposed && Visible && Editing) OpenQuickEditor(); }; _editorMenu.Items.Add(appearance);
        AddEditorMenuItems(_editorMenu);
        _editorMenu.Show(this, new Point(ModeBounds.Left, ContentTop));
    }

    protected virtual void AddEditorMenuItems(Forms.ContextMenuStrip menu) { }

    private async Task ChangeModeAsync(OverlayDisplayMode mode)
    {
        try
        {
            _quickEditor?.Close();
            await SaveInlineAsync();
            await ViewModel.UpdateOverlayRegionAsync(Kind, current => current with { Mode = mode, ModeVersion = 1 });
        }
        catch (Exception ex) { ViewModel.ReportSettingsError("保存显示类型失败：" + ex.Message); }
    }

    private void OpenQuickEditor()
    {
        if (_quickEditor is { IsDisposed: false }) { _quickEditor.Activate(); return; }
        var editor = new OverlayQuickEditor(Kind, OverlayRegions.Get(ViewModel.Settings.Display, Kind), PreviewRegion, async draft =>
        {
            await ViewModel.UpdateOverlayRegionAsync(Kind, current => draft with
            { X = current.X, Y = current.Y, Width = current.Width, Height = current.Height });
        });
        _quickEditor = editor;
        editor.FormClosed += (_, _) => { if (ReferenceEquals(_quickEditor, editor)) _quickEditor = null; PreviewRegion(null); };
        editor.Show(this);
    }

    private void PreviewRegion(OverlayRegionSettings? draft)
    {
        if (IsDisposed || Disposing) return;
        var previous = RegionSettings;
        _previewRegion = draft is null ? null : draft with { X = Left, Y = Top, Width = Width, Height = Height };
        _sampleDirty = true;
        ApplySurfaceSettings(); OnRegionSettingsChanged(previous); RequestRender();
    }

    protected override void OnMouseDown(Forms.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Editing || e.Button != Forms.MouseButtons.Left) return;
        for (var index = 0; index < 3; index++)
        {
            var field = OverlayInlineControls.Field(ClientSize.Width, index);
            if (!field.Contains(e.Location)) continue;
            _quickEditor?.Close();
            if (OverlayInlineControls.Minus(field).Contains(e.Location) || OverlayInlineControls.Plus(field).Contains(e.Location))
            {
                var step = OverlayInlineControls.Range(index).Step;
                SetInlineValue(index, OverlayInlineControls.Value(RawRegion, index) + (OverlayInlineControls.Plus(field).Contains(e.Location) ? step : -step));
            }
            else if (e.Y >= field.Top + 20) { _slider = index; Capture = true; DragSlider(e.X); }
            return;
        }
    }
    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e); if (_slider >= 0 && Editing) DragSlider(e.X);
    }
    protected override async void OnMouseUp(Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != Forms.MouseButtons.Left) return;
        _slider = -1; Capture = false;
        if (!_inlineDirty) return;
        _inlinePreviewTimer.Stop(); ApplyInlinePreview(); await SaveInlineAsync();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || _slider < 0) return;
        _slider = -1; _inlinePreviewTimer.Stop(); ApplyInlinePreview(); _ = SaveInlineAsync();
    }
    private void DragSlider(int x)
    {
        var track = OverlayInlineControls.Track(OverlayInlineControls.Field(ClientSize.Width, _slider));
        var (min, max, _) = OverlayInlineControls.Range(_slider);
        SetInlineValue(_slider, (int)Math.Round(min + Math.Clamp((x - track.Left) / (double)Math.Max(1, track.Width - 1), 0, 1) * (max - min)));
    }
    private void SetInlineValue(int index, int value)
    {
        var next = OverlayInlineControls.Set(RawRegion, index, value);
        if (next == RawRegion) return;
        _inlineRegion = next; _inlineDirty = true;
        // Coalesce mouse movement, without a permanent frame or polling clock.
        if (!_inlinePreviewTimer.Enabled) _inlinePreviewTimer.Start();
    }
    private void ApplyInlinePreview()
    {
        if (IsDisposed || Disposing) return;
        var previous = RegionSettings; ApplySurfaceSettings(); _sampleDirty = true;
        OnRegionSettingsChanged(previous); RequestRender();
    }
    private Task SaveInlineAsync()
    {
        if (_inlineSaveTask is { IsCompleted: false }) return _inlineSaveTask;
        return _inlineSaveTask = SaveInlineCoreAsync();
    }
    private async Task SaveInlineCoreAsync()
    {
        if (IsDisposed || Disposing) return;
        try
        {
            while (_inlineDirty)
            {
                var draft = _inlineRegion; var enabled = _inlineEnabled; _inlineDirty = false;
                try
                {
                    await ViewModel.UpdateOverlayRegionAsync(Kind, current => draft is null ? current : current with
                    { ContentScale = draft.ContentScale, FontSize = draft.FontSize, Opacity = draft.Opacity }, enabled);
                }
                catch (Exception ex)
                {
                    _inlineRegion = null; _inlineEnabled = null; _inlineDirty = false;
                    ViewModel.ReportSettingsError("保存浮窗设置失败：" + ex.Message); break;
                }
            }
            if (!_inlineDirty) { _inlineRegion = null; _inlineEnabled = null; }
        }
        finally { ApplyInlinePreview(); }
    }

    private async Task FinishEditingAsync()
    {
        try
        {
            await SaveInlineAsync();
            await ViewModel.LockOverlayLayoutAsync();
            ViewModel.EndOverlayInteraction();
        }
        catch (Exception ex) { ViewModel.ReportSettingsError("结束浮窗调整失败：" + ex.Message); }
    }

    protected override async void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (!Editing || IsDisposed) return;
        try
        {
            await ViewModel.UpdateOverlayRegionAsync(Kind, current => current with { X = Left, Y = Top, Width = Width, Height = Height });
        }
        catch (Exception ex) { ViewModel.ReportSettingsError($"保存区域位置失败：{ex.Message}"); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _editorClock?.Stop(OnEditorFrame); ViewModel.PropertyChanged -= OnSettingsChanged; _inlinePreviewTimer.Dispose(); _samplePreview.Dispose(); _quickEditor?.Dispose(); _quickEditor = null; _editorMenu?.Dispose(); _editorMenu = null; _canvas?.Dispose(); _canvas = null; }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
