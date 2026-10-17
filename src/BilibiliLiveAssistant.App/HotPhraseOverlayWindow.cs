using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using BilibiliLiveAssistant.Core;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

internal sealed class HotPhraseOverlayWindow : Forms.Form
{
    private readonly LiveAssistantViewModel _vm;
    private readonly OverlayFrameClock _frames;
    private readonly HotPhraseSprite _sprite;
    private LayeredOverlayCanvas? _canvas;
    private bool _requested, _disposed, _loading, _queued, _clickThrough = true, _errorReported;
    private readonly Forms.ContextMenuStrip _menu = new();
    private HotPhraseSettings? _stylePreview;
    private bool Editing => _vm.OverlayInteractive || !_vm.Settings.Display.LayoutLocked;
    internal HotPhraseOverlayWindow(LiveAssistantViewModel vm, OverlayFrameClock frames)
    {
        _vm = vm; _frames = frames; _sprite = new(vm.Images, RequestRender);
        Text = "B站直播助手 · 高频气泡"; FormBorderStyle = Forms.FormBorderStyle.None; StartPosition = Forms.FormStartPosition.Manual;
        ShowInTaskbar = false; TopMost = true; AutoScaleMode = Forms.AutoScaleMode.None; MinimumSize = new(160, 64);
        _menu.Items.Add("气泡字号、表情与透明度…", null, (_, _) => EditStyle());
        _menu.Items.Add("隐藏高频提示", null, async (_, _) => await Save(s => s with { Enabled = false }));
        _menu.Items.Add("完成调整", null, (_, _) => vm.EndOverlayInteraction());
        MouseUp += (_, e) => { if (Editing && e.Button == Forms.MouseButtons.Right) _menu.Show(this, e.Location); };
        ResizeEnd += async (_, _) => { if (!_loading && !_disposed) await Save(s => s with { X = Left, Y = Top, Width = Width, Height = Height }); };
        vm.HotPhrasesChanged += RefreshContent; vm.PropertyChanged += Changed; vm.RoomHistoryReset += Reset;
        ApplyBounds();
    }
    internal object GetDiagnostics() => new { visible = Visible, bitmapBytes = _sprite.Bytes, canvasBytes = _canvas is { } c ? (long)c.Width * c.Height * 4 : 0, animationActive = _frames.IsRunning(Frame) };
    internal void ShowPanel() { _requested = true; ApplyBounds(); RefreshContent(); }
    internal void HidePanel() { _requested = false; Hide(); Release(); }
    private void Reset() { _sprite.Dispose(); RefreshContent(); }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(LiveAssistantViewModel.Settings) or nameof(LiveAssistantViewModel.OverlayInteractive))) return;
        ApplyBounds(); RefreshContent();
    }
    private void ApplyBounds()
    {
        _loading = true;
        try
        {
            var s = _vm.Settings.HotPhrases; var screen = Forms.Screen.FromPoint(new(s.X ?? 0, s.Y ?? 0)).WorkingArea;
            var width = Math.Min(s.Width, screen.Width); var height = Math.Min(s.Height, screen.Height);
            Bounds = new(Math.Clamp(s.X ?? screen.Right - width - 24, screen.Left, Math.Max(screen.Left, screen.Right - width)),
                Math.Clamp(s.Y ?? screen.Bottom - height - 24, screen.Top, Math.Max(screen.Top, screen.Bottom - height)), width, height);
            _clickThrough = !Editing;
            if (IsHandleCreated)
            { var style = GetWindowLongPtr(Handle, -20).ToInt64(); SetWindowLongPtr(Handle, -20, new(_clickThrough ? style | 0x20 : style & ~0x20L)); }
        }
        finally { _loading = false; }
    }
    private void RefreshContent()
    {
        if (_disposed || !_requested) return;
        if (!_vm.Settings.HotPhrases.Enabled && !Editing) { Hide(); Release(); return; }
        if (!Visible) Show();
        RequestRender();
    }
    private void RequestRender()
    { if (_disposed || !_requested || !Visible || !IsHandleCreated || _queued) return; _queued = true; if (!PostMessage(Handle, 0x8521, 0, 0)) _queued = false; }
    private void Frame(double elapsed) { if (!_queued) Render(); }
    private void Render()
    {
        if (_disposed || !_requested || !Visible || ClientSize.Width < 1 || ClientSize.Height < 1) return;
        try
        {
        var top = Editing ? 28 : 0; var settings = _stylePreview ?? _vm.Settings.HotPhrases;
        _sprite.Update(Editing ? HotPhraseSamples.Items : _vm.HotPhrases, settings with { Enabled = Editing || settings.Enabled },
            new(Math.Max(1, ClientSize.Width - 8), Math.Max(0, ClientSize.Height - top - 4)), Math.Clamp((ClientSize.Height - top) / HotBubbleLayouts.RowHeight(settings), 1, 5), _vm.Settings.Dashboard.FontFamily);
        if (_canvas?.Width != ClientSize.Width || _canvas.Height != ClientSize.Height) { _canvas?.Dispose(); _canvas = new(ClientSize.Width, ClientSize.Height); }
        var g = _canvas.Graphics; g.ResetClip(); g.CompositingMode = CompositingMode.SourceCopy; g.Clear(Color.Transparent); g.CompositingMode = CompositingMode.SourceOver;
        _sprite.Draw(g, new(4, top));
        if (Editing)
        {
            using var fill = new SolidBrush(Color.FromArgb(220, 36, 43, 58)); g.FillRectangle(fill, 0, 0, Width, 26);
            using var font = OverlayTextRenderer.CreateFont("Microsoft YaHei UI", 12);
            OverlayTextRenderer.Draw(g, "高频气泡 · 拖动 / 右键样式", font, new(7, 5), Color.White, false);
            using var pen = new Pen(Color.FromArgb(150, 125, 159, 202)); g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        _canvas.Present(Handle, Location, Editing ? 1 : settings.Opacity, ClientRectangle);
        if (_sprite.Moving) _frames.Start(Frame); else _frames.Stop(Frame);
        if (!_sprite.HasContent && !Editing) { _canvas.Dispose(); _canvas = null; }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _frames.Stop(Frame);
            if (!_errorReported) { _errorReported = true; RuntimeErrorLog.Record("hot-phrase-render", ex); _vm.ReportSettingsError("高频气泡绘制失败，请重新打开浮窗；错误已记入 runtime-errors.log。"); }
        }
    }
    private async Task Save(Func<HotPhraseSettings, HotPhraseSettings> update)
    { try { await _vm.UpdateHotPhrasesAsync(update); } catch (Exception ex) when (ex is not OutOfMemoryException) { _vm.ReportSettingsError("高频提示设置保存失败，请重试。"); } }
    private void EditStyle()
    {
        using var editor = new Forms.Form { Text = "高频气泡", Width = 380, Height = 490, StartPosition = Forms.FormStartPosition.CenterParent, TopMost = true };
        var panel = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, AutoScroll = true, WrapContents = false };
        editor.Controls.Add(panel); var current = _vm.Settings.HotPhrases; _stylePreview = current;
        void Slider(string label, int value, int min, int max, Func<HotPhraseSettings, int, HotPhraseSettings> update)
        {
            var heading = new Forms.Label { Text = label + " " + value, AutoSize = true };
            var slider = new Forms.TrackBar { Minimum = min, Maximum = max, Value = value, Width = 280, TickStyle = Forms.TickStyle.None };
            panel.Controls.Add(heading); panel.Controls.Add(slider);
            slider.ValueChanged += (_, _) => { heading.Text = label + " " + slider.Value; current = update(current, slider.Value); _stylePreview = current; RequestRender(); };
        }
        Slider("字号", current.FontSize, 12, 40, (s, n) => s with { FontSize = n });
        Slider("表情大小", current.EmoteSize, 16, 80, (s, n) => s with { EmoteSize = n });
        Slider("内容不透明度 %", (int)(current.Opacity * 100), 10, 100, (s, n) => s with { Opacity = n / 100d });
        Slider("背景不透明度 %", (int)(current.BackgroundOpacity * 100), 0, 100, (s, n) => s with { BackgroundOpacity = n / 100d });
        var actions = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 52, Padding = new(8), FlowDirection = Forms.FlowDirection.RightToLeft };
        var apply = new Forms.Button { Text = "保存并应用", AutoSize = true };
        var cancel = new Forms.Button { Text = "取消", AutoSize = true }; cancel.Click += (_, _) => editor.Close();
        var saving = false; editor.FormClosing += (_, args) => { if (saving) args.Cancel = true; };
        apply.Click += async (_, _) =>
        {
            if (saving || _disposed) return; saving = true; actions.Enabled = panel.Enabled = false;
            try
            {
                await _vm.UpdateHotPhrasesAsync(s => s with { FontSize = current.FontSize, EmoteSize = current.EmoteSize, Opacity = current.Opacity, BackgroundOpacity = current.BackgroundOpacity });
                saving = false; editor.Close();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _vm.ReportSettingsError("高频气泡样式保存失败，请重试。"); }
            finally { saving = false; if (!editor.IsDisposed) actions.Enabled = panel.Enabled = true; }
        };
        actions.Controls.Add(apply); actions.Controls.Add(cancel); editor.Controls.Add(actions); OverlayEditorTheme.Apply(editor);
        try { editor.ShowDialog(this); }
        finally { _stylePreview = null; RequestRender(); }
    }
    private void Release() { _queued = false; _frames.Stop(Frame); _sprite.Dispose(); _canvas?.Dispose(); _canvas = null; }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (_sprite is not null) RequestRender(); }
    protected override bool ShowWithoutActivation => true;
    protected override void OnHandleDestroyed(EventArgs e) { Release(); base.OnHandleDestroyed(e); }
    protected override void OnFormClosing(Forms.FormClosingEventArgs e)
    { if (!_disposed && e.CloseReason == Forms.CloseReason.UserClosing) { e.Cancel = true; HidePanel(); return; } base.OnFormClosing(e); }
    protected override Forms.CreateParams CreateParams { get { var c = base.CreateParams; c.ExStyle |= 0x80000 | 0x80 | 0x08000000; if (_clickThrough) c.ExStyle |= 0x20; return c; } }
    protected override void OnPaintBackground(Forms.PaintEventArgs e) { }
    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == 0x8521) { _queued = false; Render(); return; }
        if (m.Msg == 0x84 && _vm is not null && Editing)
        {
            var raw = m.LParam.ToInt64(); var p = PointToClient(new(unchecked((short)raw), unchecked((short)(raw >> 16))));
            var left = p.X < 7; var right = p.X >= ClientSize.Width - 7; var top = p.Y < 7; var bottom = p.Y >= ClientSize.Height - 7;
            m.Result = left && top ? 13 : right && top ? 14 : left && bottom ? 16 : right && bottom ? 17 : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : p.Y < 26 ? 2 : 1; return;
        }
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        { _disposed = true; _vm.HotPhrasesChanged -= RefreshContent; _vm.PropertyChanged -= Changed; _vm.RoomHistoryReset -= Reset; Release(); _menu.Dispose(); }
        base.Dispose(disposing);
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
