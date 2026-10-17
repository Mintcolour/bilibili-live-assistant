using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BilibiliLiveAssistant.Core;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

// Native alpha surface; only the visible rows own sprites and image consumers.
internal sealed class CompactOverlayWindow : Forms.Form
{
    private sealed class Row(long id, LiveEvent value, Bitmap bitmap, int width, int maxHeight, bool card) : IDisposable
    {
        public long Id = id;
        public LiveEvent Source = value, Value = value;
        public Bitmap Image = bitmap;
        public int Width = width, MaxHeight = maxHeight;
        public bool Card = card, RefreshQueued;
        public Rectangle Bounds;
        public CompactRowMotion Motion = new();
        public CancellationTokenSource Cancellation = new();
        public void Dispose() { Cancellation.Cancel(); Cancellation.Dispose(); Image.Dispose(); }
    }
    private readonly LiveAssistantViewModel _vm;
    private readonly OverlayFrameClock _frames;
    private readonly ImageAttributes _fadeAttributes = new();
    private readonly ColorMatrix _fadeMatrix = new();
    private readonly Dictionary<long, Row> _rows = new();
    private readonly Dictionary<LiveEventType, int> _cardHeights = new();
    private LayeredOverlayCanvas? _canvas;
    private bool _renderQueued, _disposed, _loadingBounds, _reading, _clickThrough = true, _errorReported;
    private long? _anchor;
    private Rectangle _listBounds;
    private bool _listReady;
    private long _latestListId;
    private static double MotionNow => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private LiveEvent? _statusRank, _statusOnline, _statusValue;
    private LiveEvent? _renderedStatus;
    private CompactStatusStrip? _statusStrip;
    private Point _statusLocation;
    private double _statusStarted;
    private bool _statusVisible, _listMoving;
    private Forms.Form? _detail;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly HotPhraseSprite _hotSprite = new();
    private readonly CompactTimeline _samples = new();
    private CompactOverlaySettings? _preview;
    private AppSettings EffectiveSettings => _preview is { } preview ? _vm.Settings with { Compact = preview } : _vm.Settings;
    private bool Editing => (_vm.OverlayInteractive || !_vm.Settings.Display.LayoutLocked) && !_reading;
    private bool Interactive => _reading || Editing;
    internal object GetDiagnostics() => new { visible = Visible, editing = Editing, reading = _reading, rows = _rows.Count,
        bitmapBytes = _rows.Values.Sum(row => (long)row.Image.Width * row.Image.Height * 4) + (_statusStrip?.Bytes ?? 0), readingOlder = _anchor is not null,
        animationActive = _frames.IsRunning(OnAnimationFrame), leavingRows = _rows.Values.Count(row => row.Motion.Leaving) };
    private CompactTimeline Timeline => Editing ? _samples : _vm.CompactOverlay.Timeline;
    public CompactOverlayWindow(LiveAssistantViewModel vm, OverlayFrameClock frames)
    {
        _vm = vm; _frames = frames; Text = "B站直播助手 · 紧凑面板"; FormBorderStyle = Forms.FormBorderStyle.None;
        StartPosition = Forms.FormStartPosition.Manual; AutoScaleMode = Forms.AutoScaleMode.None;
        ShowInTaskbar = false; TopMost = true; MinimumSize = new(240, 320);
        _menu.Items.Add("调整样式…", null, (_, _) => ShowStyleEditor());
        _menu.Items.Add("阅读实时列表", null, (_, _) => StartReading());
        _menu.Items.Add("回到最新", null, (_, _) => { _anchor = null; _listReady = false; RequestRender(); });
        _menu.Items.Add("完成调整／阅读", null, async (_, _) => await FinishInteractionAsync());
        try
        {
            ApplyBounds(); MakeSamples(); ApplyInteraction();
            vm.CompactOverlay.Changed += RequestRender; vm.RoomHistoryReset += Reset; vm.PropertyChanged += SettingsChanged;
            ResizeEnd += async (_, _) => await SaveBoundsAsync();
        }
        catch { Dispose(); throw; }
    }
    public void ShowPanel()
    {
        if (_disposed) return;
        if (!Visible) _errorReported = false;
        ApplyInteraction();
        if (!Visible) Show();
        _frames.Configure(_vm.Settings.Display.AnimationFrameRate, Forms.Screen.FromRectangle(Bounds).DeviceName);
        _vm.CompactOverlay.SetDesktopVisible(!Editing); RequestRender();
    }
    public void HidePanel()
    {
        if (Visible) Hide();
        _vm.CompactOverlay.SetDesktopVisible(false); _detail?.Close();
        _renderQueued = false; ReleaseRows(); _canvas?.Dispose(); _canvas = null;
    }
    public void StartReading()
    { _reading = true; _statusValue = null; if (!_vm.OverlayInteractive) _vm.ToggleOverlayInteraction(); _vm.CompactOverlay.SetDesktopVisible(Visible); ApplyInteraction(); ReleaseRows(); RequestRender(); }
    private void Reset() { _anchor = null; _statusRank = _statusOnline = _statusValue = null; _detail?.Close(); ReleaseRows(); RequestRender(); }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(LiveAssistantViewModel.Settings) or nameof(LiveAssistantViewModel.OverlayInteractive))) return;
        if (!_vm.OverlayInteractive) _reading = false;
        if (args.PropertyName == nameof(LiveAssistantViewModel.Settings)) ApplyBounds();
        if (Visible) _vm.CompactOverlay.SetDesktopVisible(!Editing);
        ApplyInteraction(); MakeSamples(); _statusValue = null; _cardHeights.Clear(); ReleaseRows(); RequestRender();
    }
    private void ApplyBounds()
    {
        _loadingBounds = true;
        try
        {
            var s = _vm.Settings.Compact; var screen = Forms.Screen.FromPoint(new(s.X ?? 0, s.Y ?? 0)).WorkingArea;
            var width = Math.Min(s.Width, screen.Width); var height = Math.Min(s.Height, screen.Height);
            Bounds = new(Math.Clamp(s.X ?? screen.Right - width - 24, screen.Left, Math.Max(screen.Left, screen.Right - width)),
                Math.Clamp(s.Y ?? screen.Top + 24, screen.Top, Math.Max(screen.Top, screen.Bottom - height)), width, height);
        }
        finally { _loadingBounds = false; }
    }
    private async Task SaveBoundsAsync()
    {
        if (_loadingBounds || _disposed || !Interactive) return;
        _frames.Configure(_vm.Settings.Display.AnimationFrameRate, Forms.Screen.FromRectangle(Bounds).DeviceName);
        try { await _vm.UpdateCompactAsync(s => s with { X = Left, Y = Top, Width = Width, Height = Height }); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _vm.ReportSettingsError("紧凑面板位置保存失败，请重试。"); }
    }
    private void ApplyInteraction()
    {
        _clickThrough = !Interactive;
        if (!IsHandleCreated) return;
        var style = GetWindowLongPtr(Handle, -20).ToInt64();
        SetWindowLongPtr(Handle, -20, new(_clickThrough ? style | 0x20 : style & ~0x20L));
    }
    protected override Forms.CreateParams CreateParams
    {
        get
        {
            // Control's base constructor calls this before _vm is assigned.
            // Keep window-style calculation independent of the view model.
            var value = base.CreateParams;
            value.ExStyle |= 0x80000 | 0x80 | 0x08000000;
            if (_clickThrough) value.ExStyle |= 0x20;
            return value;
        }
    }
    protected override bool ShowWithoutActivation => true;
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyInteraction(); RequestRender(); }
    protected override void OnPaintBackground(Forms.PaintEventArgs e) { }
    protected override void OnFormClosing(Forms.FormClosingEventArgs e)
    {
        if (!_disposed && e.CloseReason == Forms.CloseReason.UserClosing) { e.Cancel = true; HidePanel(); return; }
        base.OnFormClosing(e);
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_frames is not null) { _frames.Stop(OnAnimationFrame); _listReady = false; }
        RequestRender();
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        _renderQueued = false; ReleaseRows(); _canvas?.Dispose(); _canvas = null;
        base.OnHandleDestroyed(e);
    }
    private void RequestRender()
    {
        if (_disposed || !Visible || !IsHandleCreated || _renderQueued) return;
        _renderQueued = true; if (!PostMessage(Handle, 0x8519, 0, 0)) _renderQueued = false;
    }
    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == 0x8519) { _renderQueued = false; Render(); return; }
        if (m.Msg == 0x84 && Interactive)
        {
            var raw = m.LParam.ToInt64(); var p = PointToClient(new(unchecked((short)(raw & 0xffff)), unchecked((short)((raw >> 16) & 0xffff))));
            var edge = 8; var left = p.X < edge; var right = p.X >= ClientSize.Width - edge; var top = p.Y < edge; var bottom = p.Y >= ClientSize.Height - edge;
            m.Result = left && top ? 13 : right && top ? 14 : left && bottom ? 16 : right && bottom ? 17 : left ? 10 : right ? 11 : top ? 12 : bottom ? 15
                : p.Y < 34 && p.X < ClientSize.Width - 140 ? 2 : 1; return;
        }
        base.WndProc(ref m);
    }
    private void Render()
    {
        if (_disposed || !Visible || ClientSize.Width < 1 || ClientSize.Height < 1) return;
        try
        {
            if (_canvas?.Width != ClientSize.Width || _canvas.Height != ClientSize.Height) { _canvas?.Dispose(); _canvas = new(ClientSize.Width, ClientSize.Height); }
            var g = _canvas.Graphics; g.ResetClip(); g.CompositingMode = CompositingMode.SourceCopy; g.Clear(Color.Transparent); g.CompositingMode = CompositingMode.SourceOver;
            var config = EffectiveSettings.Compact; var timeline = Timeline; var keep = new HashSet<long>();
            var now = MotionNow; var seconds = config.ListAnimationDurationMs / 1000d;
            var top = Interactive ? 36 : 4; var bottom = ClientSize.Height - 4;
            _statusVisible = false;
            if (config.BackgroundOpacity > 0)
            { using var brush = new SolidBrush(OverlayTextRenderer.Color(config.BackgroundColor, config.BackgroundOpacity)); g.FillRectangle(brush, ClientRectangle); }
            if (Interactive)
            {
                using var brush = new SolidBrush(Color.FromArgb(225, 35, 42, 57)); g.FillRectangle(brush, 0, 0, ClientSize.Width, 32);
                using var font = OverlayTextRenderer.CreateFont("Microsoft YaHei UI", 12);
                OverlayTextRenderer.Draw(g, Editing ? "紧凑面板 · 示例" : "紧凑面板 · 阅读", font, new(10, 7), Color.White, false);
                OverlayTextRenderer.Draw(g, "样式   最新   完成", font, new(ClientSize.Width - 138, 7), Color.White, false);
            }
            if (config.ShowStatus && OverlayRegions.Enabled(_vm.Settings.Display, OverlayRegionKind.Status))
            {
                if (_statusValue is null || !ReferenceEquals(_statusRank, _vm.LatestRank) || !ReferenceEquals(_statusOnline, _vm.LatestOnline))
                {
                    _statusRank = _vm.LatestRank; _statusOnline = _vm.LatestOnline;
                    IReadOnlyList<LiveAudienceMetric> metrics = Editing ? CompactExamples.Bulletin().Metrics
                        : OverlayBulletinRenderer.AudienceMetrics(_statusRank, _statusOnline, true);
                    IReadOnlyList<LiveRankMember> members = Editing ? CompactExamples.Bulletin().Members
                        : _statusRank is { } latest ? _vm.GetRankMembers(latest, 3) : [];
                    _statusValue = new(LiveEventType.Rank, "", "", "#B998FF", Metadata: new Dictionary<string, object?> { ["room_bulletin"] = new LiveRoomBulletin(metrics, members, members.Count > 0) });
                }
                var statusWidth = Math.Max(120, ClientSize.Width - 12);
                if (_statusStrip is null || !ReferenceEquals(_renderedStatus, _statusValue) || _statusStrip.Width != statusWidth)
                {
                    var bulletin = (LiveRoomBulletin)_statusValue.Extra["room_bulletin"]!;
                    var replacement = OverlayBulletinRenderer.CreateCompact(bulletin, ObsOverlayProjection.Region(EffectiveSettings, _statusValue), _vm.Settings.Dashboard, _vm.Images, statusWidth);
                    if (_statusStrip is null) _statusStarted = now;
                    _statusStrip?.Dispose(); _statusStrip = replacement; _renderedStatus = _statusValue;
                }
                _statusLocation = new(6, top); _statusVisible = true;
                _statusStrip.Draw(g, _statusLocation, now - _statusStarted); top += _statusStrip.Height + 4;
            }
            else { _statusStrip?.Dispose(); _statusStrip = null; _renderedStatus = null; }
            if (!Editing && config.ShowList && OverlayRegions.Enabled(_vm.Settings.Display, OverlayRegionKind.Danmaku))
            {
                var height = HotPhraseSprite.Height(_vm.HotPhrases, Math.Min(ClientSize.Height / 4, Math.Max(0, bottom - top - 120)), config.FontSize);
                _hotSprite.Draw(g, _vm.HotPhrases, new(6, top, Math.Max(1, ClientSize.Width - 12), height),
                    ObsOverlayProjection.Region(EffectiveSettings, new(LiveEventType.Danmaku, "", "", "#FFFFFF")).FontFamily, config.FontSize);
                if (height > 0) top += height + 4;
            }
            else _hotSprite.Dispose();
            void AtTop(CompactMessage? item, int maxHeight, bool show, long key)
            {
                if (!show || item is null || bottom - top < 60) return;
                var minimum = MinimumCardHeight(item.Value);
                if (bottom - top < minimum) return;
                var row = GetRow(key, item.Value, Math.Min(Math.Max(maxHeight, minimum), Math.Max(minimum, (bottom - top) / 2)), true); keep.Add(key);
                row.Bounds = new(6, top, row.Image.Width, row.Image.Height); DrawRow(g, row); top += row.Image.Height + 4;
            }
            AtTop(timeline.SuperChat, config.FontSize * (config.SuperChatLines + 2) + 24, config.ShowSuperChat && OverlayRegions.Enabled(_vm.Settings.Display, OverlayRegionKind.SuperChat), -1);
            AtTop(timeline.Gift, config.FontSize * 3 + 24, config.ShowGift && OverlayRegions.Enabled(_vm.Settings.Display, OverlayRegionKind.Gift), -2);
            void AtBottom(CompactMessage? item, bool show, long key)
            {
                if (!show || item is null || bottom - top < 44) return;
                var row = GetRow(key, item.Value, Math.Min(68, config.FontSize * 2 + 20), false); keep.Add(key);
                bottom -= row.Image.Height; row.Bounds = new(6, bottom, row.Image.Width, row.Image.Height); DrawRow(g, row); bottom -= 4;
            }
            var interactions = OverlayRegions.Enabled(_vm.Settings.Display, OverlayRegionKind.Interaction);
            AtBottom(timeline.Follow, config.ShowFollow && interactions, -4); AtBottom(timeline.Entry, config.ShowEntry && interactions, -3);
            var nextBounds = new Rectangle(6, top, Math.Max(1, ClientSize.Width - 12), Math.Max(0, bottom - top));
            var animate = config.ListAnimationEnabled && _listReady && _anchor is null && nextBounds.Width == _listBounds.Width;
            _listBounds = nextBounds;
            var moving = false;
            if (config.ShowList && _listBounds.Height > 0)
            {
                var rows = timeline.Rows; var end = rows.Count - 1;
                if (_anchor is { } anchor) { var index = rows.ToList().FindIndex(r => r.Id == anchor); if (index >= 0) end = index; else _anchor = null; }
                var y = bottom; var state = g.Save(); g.SetClip(_listBounds);
                for (var i = end; i >= 0 && y > top && keep.Count < 36; i--)
                {
                    var item = rows[i];
                    var minimum = MinimumCardHeight(item.Value);
                    var row = GetRow(item.Id, item.Value, Math.Max(minimum, Math.Max(100, _listBounds.Height)), true); keep.Add(item.Id);
                    y -= row.Image.Height;
                    row.Motion.Place(y, now, seconds, animate, item.Id > _latestListId);
                    DrawMovingRow(g, row, now); moving |= row.Motion.Running(now); y -= 4;
                }
                var leaving = 0; long leavingBytes = 0;
                foreach (var row in _rows.Values)
                {
                    if (row.Id <= 0 || keep.Contains(row.Id) || !animate || !row.Motion.Positioned) continue;
                    var bytes = (long)row.Image.Width * row.Image.Height * 4;
                    if (leaving >= 4 || leavingBytes + bytes > 4 * 1024 * 1024) continue;
                    var current = row.Motion.Sample(now);
                    if (!row.Motion.Leaving && (current.Y + row.Image.Height <= top || current.Y >= bottom)) continue;
                    row.Motion.Leave(top, row.Image.Height, now, seconds);
                    row.Cancellation.Cancel();
                    if (!row.Motion.Running(now)) continue;
                    keep.Add(row.Id); leaving++; leavingBytes += bytes;
                    DrawMovingRow(g, row, now); moving = true;
                }
                g.Restore(state);
            }
            _latestListId = timeline.Rows.Count > 0 ? timeline.Rows[^1].Id : 0;
            _listReady = config.ShowList && _listBounds.Height > 0;
            foreach (var key in _rows.Keys.Where(k => !keep.Contains(k)).ToArray()) { _rows[key].Dispose(); _rows.Remove(key); }
            _canvas.Present(Handle, Location, config.Opacity, ClientRectangle);
            _listMoving = moving;
            if (moving || _statusVisible && _statusStrip?.Moving == true) _frames.Start(OnAnimationFrame); else _frames.Stop(OnAnimationFrame);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _frames.Stop(OnAnimationFrame); ReportRenderError(ex); }
    }
    private void OnAnimationFrame(double elapsed)
    {
        if (_disposed || !Visible) { _frames.Stop(OnAnimationFrame); return; }
        if (_renderQueued) return;
        if (_listMoving && _anchor is null) { Render(); return; }
        if (!_statusVisible || _statusStrip?.Moving != true || _canvas is null) { _frames.Stop(OnAnimationFrame); return; }
        try
        {
            var strip = _statusStrip;
            var dirty = new Rectangle(_statusLocation.X + strip.RankLeft, _statusLocation.Y, strip.RankWidth, strip.Height);
            var g = _canvas.Graphics; var saved = g.Save();
            try
            {
                g.SetClip(dirty); g.CompositingMode = CompositingMode.SourceCopy; g.FillRectangle(Brushes.Transparent, dirty);
                g.CompositingMode = CompositingMode.SourceOver;
                var config = EffectiveSettings.Compact;
                if (config.BackgroundOpacity > 0)
                { using var brush = new SolidBrush(OverlayTextRenderer.Color(config.BackgroundColor, config.BackgroundOpacity)); g.FillRectangle(brush, dirty); }
                strip.Draw(g, _statusLocation, MotionNow - _statusStarted);
            }
            finally { g.Restore(saved); }
            _canvas.Present(Handle, Location, EffectiveSettings.Compact.Opacity, dirty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _frames.Stop(OnAnimationFrame); ReportRenderError(ex); }
    }
    private void DrawMovingRow(Graphics graphics, Row row, double now)
    {
        var (y, alpha) = row.Motion.Sample(now);
        row.Bounds = new(6, (int)Math.Round(y), row.Image.Width, row.Image.Height);
        if (alpha >= .999f) { OverlayBitmapBlitter.Draw(graphics, row.Image, 6, y, _listBounds); return; }
        if (alpha <= .001f) return;
        var sourceTop = Math.Clamp(_listBounds.Top - row.Bounds.Top, 0, row.Image.Height);
        var sourceBottom = Math.Clamp(_listBounds.Bottom - row.Bounds.Top, 0, row.Image.Height);
        if (sourceBottom <= sourceTop) return;
        _fadeMatrix.Matrix33 = alpha; _fadeAttributes.SetColorMatrix(_fadeMatrix);
        graphics.DrawImage(row.Image, new Rectangle(6, row.Bounds.Top + sourceTop, row.Image.Width, sourceBottom - sourceTop),
            0, sourceTop, row.Image.Width, sourceBottom - sourceTop, GraphicsUnit.Pixel, _fadeAttributes);
    }
    private void ReportRenderError(Exception exception)
    {
        if (_errorReported) return;
        _errorReported = true;
        RuntimeErrorLog.Record("compact-render", exception);
        _vm.ReportSettingsError("紧凑面板绘制失败，请重新打开浮窗；错误已记入 runtime-errors.log。");
    }
    private int MinimumCardHeight(LiveEvent value)
    {
        if (_cardHeights.TryGetValue(value.Type, out var height)) return height;
        var region = ObsOverlayProjection.Region(EffectiveSettings, value); var style = _vm.Settings.Dashboard;
        height = OverlayMessageRenderer.MinimumCardHeight(region, style, value.Type is LiveEventType.Gift or LiveEventType.Guard ? 2 : 1);
        _cardHeights[value.Type] = height; return height;
    }
    private Row GetRow(long key, LiveEvent value, int maxHeight, bool card)
    {
        var width = Math.Max(120, ClientSize.Width - 12);
        if (_rows.TryGetValue(key, out var row) && ReferenceEquals(row.Source, value) && row.Width == width && row.MaxHeight == maxHeight && row.Card == card) return row;
        var bitmap = CreateBitmap(value, width, maxHeight, card, key > 0);
        var motion = row?.Motion;
        if (row is not null) { row.Dispose(); _rows.Remove(key); }
        row = new(key, value, bitmap, width, maxHeight, card);
        if (motion is not null) row.Motion = motion;
        _rows.Add(key, row);
        if (!Editing && !value.Extra.ContainsKey("room_bulletin")) _ = LoadImagesAsync(row); return row;
    }
    private Bitmap CreateBitmap(LiveEvent value, int width, int height, bool card, bool inline)
    {
        var region = ObsOverlayProjection.Region(EffectiveSettings, value);
        return OverlayMessageRenderer.Render(value, region, _vm.Settings.Dashboard, _vm.Images, width, height, card, background: false, inlineHeader: inline);
    }
    private void DrawRow(Graphics graphics, Row row) => graphics.DrawImageUnscaled(row.Image, row.Bounds.Location);
    private async Task LoadImagesAsync(Row row)
    {
        bool Current() => !_disposed && Visible && !row.Motion.Leaving && _rows.GetValueOrDefault(row.Id) == row;
        try
        {
            var token = row.Cancellation.Token;
            row.Value = await OverlayMessageRenderer.ResolveAvatarAsync(row.Source, _vm.Settings.Dashboard, _vm.Images, token);
            if (!Current()) return;
            var region = ObsOverlayProjection.Region(_vm.Settings, row.Value);
            foreach (var url in OverlayMessageRenderer.ImageUrls(row.Value, _vm.Settings.Dashboard).Distinct().Take(16))
            {
                if (!Current()) return;
                var role = OverlayMessageRenderer.ImageRole(row.Value, url);
                await _vm.Images.GetStaticForViewAsync(url, token, OverlayMessageRenderer.ImageDecodeLimit(row.Value, url, _vm.Settings.Dashboard, region), role);
                if (!Current()) return;
                if (!row.RefreshQueued)
                {
                    row.RefreshQueued = true;
                    BeginInvoke((Action)(() =>
                    {
                        row.RefreshQueued = false; if (!Current()) return;
                        try
                        {
                            var replacement = CreateBitmap(row.Value, row.Width, row.MaxHeight, row.Card, row.Id > 0);
                            row.Image.Dispose(); row.Image = replacement; RequestRender();
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { ReportRenderError(ex); }
                    }));
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    protected override void OnMouseWheel(Forms.MouseEventArgs e)
    {
        base.OnMouseWheel(e); if (!Interactive || !_listBounds.Contains(e.Location)) return;
        var rows = Timeline.Rows; if (rows.Count == 0) return;
        var index = _anchor is { } anchor ? rows.ToList().FindIndex(r => r.Id == anchor) : rows.Count - 1;
        index = Math.Clamp(index + (e.Delta > 0 ? -1 : 1), 0, rows.Count - 1); _anchor = index == rows.Count - 1 ? null : rows[index].Id;
        _listReady = false; _frames.Stop(OnAnimationFrame); RequestRender();
    }
    protected override void OnMouseUp(Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e); if (!Interactive) return;
        if (e.Button == Forms.MouseButtons.Right) { _menu.Show(this, e.Location); return; }
        if (e.Y < 34)
        {
            if (e.X >= ClientSize.Width - 48) _ = FinishInteractionAsync();
            else if (e.X >= ClientSize.Width - 93) { _anchor = null; _listReady = false; RequestRender(); }
            else if (e.X >= ClientSize.Width - 140) ShowStyleEditor();
            return;
        }
        var row = _rows.Values.FirstOrDefault(r => r.Bounds.Contains(e.Location));
        if (row is null) return;
        _detail?.Close();
        var detail = _detail = new Forms.Form { Text = row.Value.Username, Width = 520, Height = 420, StartPosition = Forms.FormStartPosition.CenterScreen, TopMost = true };
        var detailFont = new Font("Microsoft YaHei UI", 12);
        detail.FormClosed += (_, _) => { detailFont.Dispose(); if (ReferenceEquals(_detail, detail)) _detail = null; };
        detail.Controls.Add(new Forms.TextBox { Multiline = true, ReadOnly = true, Dock = Forms.DockStyle.Fill, ScrollBars = Forms.ScrollBars.Vertical,
            Text = row.Value.Content, Font = detailFont, WordWrap = true }); detail.Show(this);
    }
    private void ShowStyleEditor()
    {
        var wasReading = _reading; _reading = false; _vm.CompactOverlay.SetDesktopVisible(!Editing); MakeSamples(); ReleaseRows(); RequestRender();
        try
        {
            using var editor = new CompactQuickEditor(_vm, value => { _preview = value; _cardHeights.Clear(); ReleaseRows(); RequestRender(); }, () =>
            {
                if (!Editing) return;
                _samples.AddBatch([new LiveEvent(LiveEventType.Danmaku, "示例观众", "新弹幕滑入，已有消息平滑上移。", "#FFFFFF")], DateTimeOffset.UtcNow, EffectiveSettings);
                RequestRender();
            });
            editor.ShowDialog(this);
        }
        finally { _preview = null; _cardHeights.Clear(); _reading = wasReading; _vm.CompactOverlay.SetDesktopVisible(Visible && !Editing); ReleaseRows(); RequestRender(); }
    }
    private async Task FinishInteractionAsync()
    {
        try { _reading = false; await _vm.LockOverlayLayoutAsync(); _vm.EndOverlayInteraction(); ApplyInteraction(); _vm.CompactOverlay.SetDesktopVisible(Visible && !Editing); RequestRender(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _vm.ReportSettingsError("浮窗锁定失败，请重试。"); }
    }
    private void MakeSamples()
    {
        _samples.Clear(); if (!Editing) return;
        var now = DateTimeOffset.UtcNow;
        var values = CompactExamples.Messages();
        _samples.AddBatch(values, now, _vm.Settings with { Display = OverlayRegions.With(_vm.Settings.Display, OverlayRegionKind.Gift,
            OverlayRegions.Get(_vm.Settings.Display, OverlayRegionKind.Gift) with { MergeSeconds = 0 }) });
    }
    private void ReleaseRows()
    {
        _hotSprite.Dispose();
        _frames.Stop(OnAnimationFrame); _listReady = false; _latestListId = 0;
        _statusVisible = _listMoving = false; _statusStrip?.Dispose(); _statusStrip = null; _renderedStatus = null;
        foreach (var row in _rows.Values) row.Dispose(); _rows.Clear();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true; _vm.CompactOverlay.SetDesktopVisible(false);
            _vm.CompactOverlay.Changed -= RequestRender; _vm.RoomHistoryReset -= Reset; _vm.PropertyChanged -= SettingsChanged;
            _detail?.Close(); ReleaseRows(); _canvas?.Dispose(); _canvas = null; _menu.Dispose(); _fadeAttributes.Dispose();
        }
        base.Dispose(disposing);
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint message, nint wParam, nint lParam);
}

internal static class CompactExamples
{
    public static LiveRoomBulletin Bulletin() => new(
        [new(LiveAudienceMetricKind.EnergyMembers, "1,234"), new(LiveAudienceMetricKind.Watched, "2.4万")],
        [new(1, 0, "示例舰长", new() { GuardLevel = 3, MedalName = "粉丝牌", MedalLevel = 12 }),
         new(2, 0, "示例观众", new() { MedalName = "粉丝牌", MedalLevel = 8 }),
         new(3, 0, "另一位观众", new() { GuardLevel = 3 })], true);

    public static LiveEvent[] Messages() =>
    [
        new LiveEvent(LiveEventType.SuperChat, "示例观众", "谢谢陪伴，今天的直播很精彩！这是固定区中的醒目留言。", "#FFD700", Appearance: new() { MedalName = "粉丝牌", MedalLevel = 12, GuardLevel = 3 }) { HistoryFields = new(AmountMilliYuan: 100000) },
        new LiveEvent(LiveEventType.Gift, "示例舰长", "小电视飞船 × 2", "#FF8ACA", Appearance: new() { GuardLevel = 3 }) { HistoryFields = new(AmountMilliYuan: 200000) },
        new(LiveEventType.Danmaku, "示例观众", "这里是聊天列表，新消息从底部出现。", "#FFFFFF"),
        new LiveEvent(LiveEventType.Enter, "刚到的朋友", "进入直播间", "#71C6A6") { HistoryFields = new(InteractionKind: 1) },
        new LiveEvent(LiveEventType.Enter, "新朋友", "关注了主播", "#FF8AB3") { HistoryFields = new(InteractionKind: 2) },
    ];
}
