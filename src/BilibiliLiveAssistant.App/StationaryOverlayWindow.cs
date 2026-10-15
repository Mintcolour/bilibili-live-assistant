using System.Drawing;
using BilibiliLiveAssistant.Core;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

// Cached, event-driven surfaces. Queues carry data; only visible slots own bitmaps or downloads.
internal sealed class StationaryOverlayWindow : OverlaySurface
{
    internal object GetDiagnostics() => new { kind = Kind.ToString(), visible = Visible, appearance = DiagnosticAppearance, views = _views.Count,
        queued = _queue.Count, listItems = _list.Count, rankItems = _ranking.Count,
        bitmapBytes = _views.Values.Sum(view => view.Image is { } image ? (long)image.Width * image.Height * 4 : 0) };
    private sealed class TimedCard(LiveEvent value, DateTimeOffset displayed, DateTimeOffset expires)
    {
        public LiveEvent Value { get; } = value;
        public DateTimeOffset Displayed { get; } = displayed;
        public DateTimeOffset Expires { get; set; } = expires;
    }
    private sealed class CardView(LiveEvent value, Rectangle bounds, string caption) : IDisposable
    {
        public LiveEvent SourceValue { get; } = value;
        public LiveEvent Value { get; set; } = value;
        public Rectangle Bounds { get; } = bounds;
        public string Caption { get; } = caption;
        public Bitmap? Image;
        public OverlayMediaAttachment? Media;
        public CancellationTokenSource Cancellation { get; } = new();
        public void Dispose() { Cancellation.Cancel(); Cancellation.Dispose(); Media?.Dispose(); Image?.Dispose(); }
    }
    private readonly Forms.Timer _wake = new();
    private readonly TimedOverlayQueue _queue = new();
    private readonly TimedCard?[] _timed = new TimedCard?[6];
    private readonly DanmakuCardBoard _board = new();
    private readonly List<LiveEvent> _list = new(100), _ranking = new(12);
    private readonly Dictionary<int, CardView> _views = new();
    private LiveEvent? _online, _rank;
    private int _offset, _hover = -1;
    private bool _disposed, _dirty, _rebuilding;
    private DateTimeOffset? _intakeDue;
    private DateTimeOffset _nextDisplayAt;
    private Forms.Form? _detail;
    public OverlayRegionKind RegionKind => Kind;
    private bool Timed => RegionSettings.Mode == OverlayDisplayMode.Cards;
    protected override bool HasLiveContent => _views.Count > 0;
    private bool GridMode => RegionSettings.Mode == OverlayDisplayMode.Grid;
    private int Columns => GridMode ? Math.Max(1, Math.Min(RegionSettings.CardColumns, (ClientSize.Width - 8) / 208)) : 1;
    private int CellGap => Kind == OverlayRegionKind.Status ? OverlayBulletinRenderer.PanelGap : 8;
    private int CellHeight => Kind == OverlayRegionKind.Status ? OverlayBulletinRenderer.PanelRowHeight(RegionSettings, ViewModel.Settings.Dashboard)
        : Math.Max(RegionSettings.CardHeight, RegionSettings.FontSize * 3 + 24);
    private int Capacity => GridMode && ClientSize.Width < 216 ? 0 : Math.Clamp(Math.Min((ClientSize.Height - ContentTop) / (CellHeight + CellGap) * Columns,
        16 * 1024 * 1024 / Math.Max(1, (ClientSize.Width / Columns) * CellHeight * 4)), 0,
        Timed ? Math.Min(6, RegionSettings.MaxVisible) : 32);
    private DashboardSettings Style => ViewModel.Settings.Dashboard with
    { BackgroundColor = RegionSettings.BackgroundColor, BackgroundOpacity = RegionSettings.BackgroundOpacity, TextColor = RegionSettings.Color, TextOpacity = 1 };

    public StationaryOverlayWindow(LiveAssistantViewModel vm, OverlayRegionKind kind) : base(vm, kind)
    {
        _queue.Dropped += vm.RecordOverlayDropped;
        _wake.Tick += (_, _) => Process();
        vm.RoomHistoryReset += ResetContent;
        vm.Dashboard.Cleared += ResetContent;
    }
    public void ShowRegion()
    {
        if (Visible) return;
        ShowSurface();
        if (Editing) return;
        if (Kind == OverlayRegionKind.Status) { _online = ViewModel.LatestOnline; _rank = ViewModel.LatestRank; RefreshRanking(); }
        _dirty = true; Process();
    }
    public void HideRegion() { if (!Visible) return; ResetContent(); Hide(); }
    public void EnqueueBatch(IReadOnlyList<LiveEvent> batch)
    {
        if (!Visible || _disposed || Editing || batch.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var value in batch)
        {
            if (Kind == OverlayRegionKind.Status)
            { if (value.Type == LiveEventType.Online) _online = value.Extra.GetValueOrDefault("sample") is true ? value : ViewModel.LatestOnline ?? value; if (value.Type == LiveEventType.Rank) _rank = value; }
            else if (Timed) _queue.Add(value, now, RegionSettings.MergeSeconds);
            else if (!GridMode)
            {
                if (_offset > 0) _offset++;
                _list.Insert(0, value); if (_list.Count > 100) _list.RemoveAt(100);
            }
        }
        if (GridMode && Capacity > 0) { _board.Resize(Capacity); _board.AddBatch(batch, ViewModel.Settings.Cards); }
        _dirty = true;
        _intakeDue ??= now.AddMilliseconds(Math.Max(RegionSettings.UpdateIntervalMs, Timed ? 0 : RegionSettings.DisplayIntervalSeconds * 1000));
        Schedule();
    }
    private void RefreshRanking()
    {
        var previous = _ranking.ToArray(); _ranking.Clear();
        var metrics = OverlayBulletinRenderer.AudienceMetrics(_rank, _online, RegionSettings.ShowAudienceStats);
        if (metrics.Count > 0) _ranking.Add(new(LiveEventType.Rank, "", string.Join(" | ", metrics.Select(m => m.Value)), RegionSettings.Color,
            Metadata: new Dictionary<string, object?> { ["room_bulletin"] = new LiveRoomBulletin(metrics, [], false) }));
        if (_rank is not null) _ranking.AddRange(ViewModel.GetRankMembers(_rank, RegionSettings.RankCount).Select(m => OverlayBulletinRenderer.MemberEvent(m, RegionSettings.Color)));
        for (var i = 0; i < Math.Min(previous.Length, _ranking.Count); i++)
        {
            var a = previous[i]; var b = _ranking[i];
            if (a.Type == b.Type && a.Username == b.Username && a.Uid == b.Uid && a.Content == b.Content && a.Appearance == b.Appearance
                && Equals(a.Extra.GetValueOrDefault("rank_position"), b.Extra.GetValueOrDefault("rank_position"))) _ranking[i] = a;
        }
    }
    private void Process()
    {
        _wake.Stop(); if (_disposed || !Visible || Editing) return;
        var now = DateTimeOffset.UtcNow;
        var changed = _dirty && (_intakeDue is null || _intakeDue <= now);
        if (changed) { _dirty = false; _intakeDue = null; if (Kind == OverlayRegionKind.Status) RefreshRanking(); }
        if (Timed)
        {
            _queue.Expire(now);
            for (var i = 0; i < _timed.Length; i++) if (_timed[i] is { } item && item.Expires <= now) { _timed[i] = null; changed = true; }
            for (var i = 0; i < Capacity; i++)
            {
                if (now < _nextDisplayAt) break;
                if (_timed[i] is not null) continue;
                var next = _queue.Take(now, Kind == OverlayRegionKind.SuperChat); if (next is null) break;
                var seconds = next.Type == LiveEventType.Guard ? RegionSettings.GuardDurationSeconds : RegionSettings.DurationSeconds;
                _timed[i] = new(next, now, now.AddSeconds(seconds)); changed = true;
                _nextDisplayAt = now.AddSeconds(RegionSettings.DisplayIntervalSeconds);
            }
        }
        if (changed) Rebuild();
        Schedule();
    }
    private void Schedule()
    {
        _wake.Stop(); if (_disposed || !Visible || Editing) return;
        DateTimeOffset? due = _intakeDue;
        void Include(DateTimeOffset? at) { if (at is not null && (due is null || at < due)) due = at; }
        if (Timed)
        {
            foreach (var card in _timed) if (card is not null) Include(card.Expires);
            Include(_queue.NextWake(false));
            if (Enumerable.Range(0, Capacity).Any(i => _timed[i] is null) && _queue.NextWake(true) is { } ready)
                Include(ready < _nextDisplayAt ? _nextDisplayAt : ready);
        }
        if (due is null) return;
        _wake.Interval = (int)Math.Clamp(Math.Ceiling((due.Value - DateTimeOffset.UtcNow).TotalMilliseconds), 1, int.MaxValue); _wake.Start();
    }
    private void Rebuild()
    {
        if (_disposed || !Visible || _rebuilding) return; _rebuilding = true;
        try
        {
            var capacity = Capacity;
            if (GridMode) _board.Resize(Math.Max(1, capacity));
            var data = Kind == OverlayRegionKind.Status ? _ranking : _list;
            _offset = Math.Clamp(_offset, 0, Math.Max(0, data.Count - Math.Max(1, capacity)));
            var keep = new HashSet<int>();
            for (var i = 0; i < capacity; i++)
            {
                var value = Timed ? _timed[i]?.Value : GridMode ? _board.Slots[i]?.Record.Event : data.Skip(_offset + i).FirstOrDefault();
                if (value is null) continue;
                var caption = GridMode ? $"×{_board.Slots[i]!.Count}" : "";
                var width = Math.Max(120, (ClientSize.Width - 16 - (Columns - 1) * 8) / Columns);
                var bounds = new Rectangle(8 + i % Columns * (width + 8), ContentTop + i / Columns * (CellHeight + CellGap), width, CellHeight);
                keep.Add(i);
                if (_views.TryGetValue(i, out var existing) && ReferenceEquals(existing.SourceValue, value) && existing.Bounds == bounds && existing.Caption == caption) continue;
                Release(i); var view = new CardView(value, bounds, caption); _views.Add(i, view);
                AdmitMedia(view); UpdateBitmap(view); _ = LoadImagesAsync(i, view);
                if (view.Media is { } media) _ = media.LoadAsync(() => { if (Current(i, view)) { UpdateBitmap(view); RequestRender(view.Bounds); } });
            }
            foreach (var key in _views.Keys.Where(k => !keep.Contains(k)).ToArray()) Release(key);
            RequestRender();
        }
        finally { _rebuilding = false; }
    }
    private void AdmitMedia(CardView view)
    {
        if (Kind is not (OverlayRegionKind.Danmaku or OverlayRegionKind.SuperChat) || view.Value.MediaReferences.Count == 0) return;
        var settings = ViewModel.Settings.MediaPreview; var body = Math.Max(60, RegionSettings.FontSize * 2 + 32);
        if (settings.Width > view.Bounds.Width || OverlayMediaAttachment.RequiredHeight(body, settings, RegionSettings) > view.Bounds.Height - (view.Caption.Length > 0 ? 22 : 0) || !ViewModel.AcquireOverlayMedia()) return;
        view.Media = new(ViewModel, view.Value.MediaReferences[0], body, RegionSettings);
    }
    private void UpdateBitmap(CardView view)
    {
        var region = view.Value.Type is LiveEventType.Gift or LiveEventType.Guard ? RegionSettings with { Bold = true } : RegionSettings;
        var captionHeight = view.Caption.Length > 0 ? 22 : 0;
        using var body = OverlayMessageRenderer.Render(view.Value, region, Style, ViewModel.Images, view.Bounds.Width,
            view.Media?.BodyHeight ?? view.Bounds.Height - captionHeight, card: Kind != OverlayRegionKind.Status, background: false);
        var replacement = new Bitmap(view.Bounds.Width, view.Bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(replacement))
        {
            OverlayMessageRenderer.DrawBackground(g, replacement.Size, region, OverlayTextRenderer.Color(region.Color));
            if (view.Media is { } media) { using var image = media.Compose(body, region); g.DrawImageUnscaled(image, 0, 0); } else g.DrawImageUnscaled(body, 0, 0);
            if (captionHeight > 0) { using var font = OverlayTextRenderer.CreateFont(region.FontFamily, 12); OverlayTextRenderer.Draw(g, view.Caption, font, new(10, replacement.Height - 20), Color.FromArgb(240, 196, 207, 226), false); }
        }
        view.Image?.Dispose(); view.Image = replacement;
    }
    private bool Current(int slot, CardView view) => !_disposed && Visible && _views.GetValueOrDefault(slot) == view;
    private async Task LoadImagesAsync(int slot, CardView view)
    {
        try
        {
            var token = view.Cancellation.Token;
            await Task.WhenAll(LoadKnownAsync(), LoadAvatarAsync());
            async Task LoadKnownAsync()
            {
                foreach (var url in OverlayMessageRenderer.ImageUrls(view.Value, Style).Distinct().Take(16))
                {
                    if (!Current(slot, view)) return;
                    var limit = OverlayMessageRenderer.ImageDecodeLimit(view.Value, url, Style, RegionSettings);
                    if (ViewModel.Images.TryGetStaticFrame(url, limit) is not null) continue;
                    if (await ViewModel.Images.GetStaticForViewAsync(url, token, limit) is null) continue;
                    if (!Current(slot, view)) return;
                    UpdateBitmap(view); RequestRender(view.Bounds);
                }
            }
            async Task LoadAvatarAsync()
            {
                var enriched = await OverlayMessageRenderer.ResolveAvatarAsync(view.Value, Style, ViewModel.Images, token);
                if (!Current(slot, view) || ReferenceEquals(enriched, view.Value)) return;
                view.Value = enriched;
                var limit = OverlayMessageRenderer.ImageDecodeLimit(view.Value, enriched.Appearance!.AvatarUrl, Style, RegionSettings);
                await ViewModel.Images.GetStaticForViewAsync(enriched.Appearance.AvatarUrl, token, limit);
                if (!Current(slot, view)) return;
                UpdateBitmap(view); RequestRender(view.Bounds);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private void Release(int slot) { if (_views.Remove(slot, out var view)) view.Dispose(); }
    private void ClearViews() { foreach (var key in _views.Keys.ToArray()) Release(key); }
    protected override void DrawContent(Graphics graphics, Rectangle clip)
    {
        foreach (var view in _views.Values) if (view.Image is not null && view.Bounds.IntersectsWith(clip)) graphics.DrawImageUnscaled(view.Image, view.Bounds.Location);
    }
    protected override void OnMouseWheel(Forms.MouseEventArgs e)
    {
        base.OnMouseWheel(e); if (Editing || Timed || GridMode) return;
        _offset += e.Delta > 0 ? -1 : 1; Rebuild();
    }
    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e); if (Editing || !GridMode) return;
        foreach (var item in _board.Slots) if (item is not null) item.Protected = false;
        _hover = _views.FirstOrDefault(v => v.Value.Bounds.Contains(e.Location)).Key;
        if (_views.TryGetValue(_hover, out var view) && view.Bounds.Contains(e.Location) && _board.Slots[_hover] is { } entry) entry.Protected = true;
        else _hover = -1;
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); ClearHover(); }
    private void ClearHover() { _hover = -1; foreach (var entry in _board.Slots) if (entry is not null) entry.Protected = false; }
    protected override void OnInteractionChanged()
    {
        ResetContent();
        if (!Editing && OverlayRegions.Enabled(ViewModel.Settings.Display, Kind) && Kind == OverlayRegionKind.Status)
        { _online = ViewModel.LatestOnline; _rank = ViewModel.LatestRank; RefreshRanking(); _dirty = true; if (Visible) Process(); }
    }
    protected override void OnMouseClick(Forms.MouseEventArgs e)
    {
        var header = Editing && e.Y < ContentTop;
        base.OnMouseClick(e);
        if (header || Editing) return;
        var pair = _views.FirstOrDefault(v => v.Value.Bounds.Contains(e.Location));
        if (pair.Value is not { } view)
        {
            if (GridMode && e.Button == Forms.MouseButtons.Right)
            { _board.ClearHidden(); RequestRender(); }
            return;
        }
        if (GridMode && _board.Slots[pair.Key] is { } entry)
        {
            if (e.Button == Forms.MouseButtons.Right)
            {
                if (!_board.Hide(entry)) ShowText("隐藏清单已满", "最多隐藏 512 项。右键空白处可清空隐藏清单。");
                Rebuild(); return;
            }
            ShowText($"重复 {entry.Count} 次", $"{view.Value.Content}\r\n\r\n" + string.Join("\r\n", entry.Senders.Values.Select(s => $"{s.Name} · UID {s.Uid} · {s.Count} 次 · {s.LastSeen.ToLocalTime():HH:mm:ss}")) + $"\r\n身份未知 {entry.UnknownCount} 次 · 未展开 {entry.UnlistedCount} 次");
        }
        else ShowText(view.Value.Username, view.Value.Content);
    }
    protected override void AddEditorMenuItems(Forms.ContextMenuStrip menu)
    {
        if (!Timed) return;
        menu.Items.Add(new Forms.ToolStripSeparator());
        var omitted = new Forms.ToolStripMenuItem($"未展示记录（{_queue.Omitted}）");
        omitted.Click += (_, _) => ShowText("未展示记录（最近 100 项）", string.Join("\r\n\r\n", _queue.Omissions.Reverse().Select(o => $"{o.At.ToLocalTime():HH:mm:ss} {o.Name} · {o.Reason}\r\n{o.Content}")));
        menu.Items.Add(omitted);
    }
    private void ShowText(string title, string text)
    {
        _detail?.Close();
        _detail = new Forms.Form { Text = title, Width = 540, Height = 420, StartPosition = Forms.FormStartPosition.CenterScreen, TopMost = true, ShowInTaskbar = false };
        var font = new Font("Microsoft YaHei UI", 12);
        _detail.Controls.Add(new Forms.TextBox { Dock = Forms.DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = Forms.ScrollBars.Vertical, Text = text, Font = font });
        _detail.FormClosed += (_, _) => font.Dispose();
        _detail.Show(this);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (_views is null) return; ClearViews(); _dirty = true; if (Visible) Process(); }
    protected override void OnRegionSettingsChanged(OverlayRegionSettings previous)
    {
        if (Editing) return;
        if (previous.Mode != RegionSettings.Mode) { ResetContent(); return; }
        if (previous.DisplayIntervalSeconds != RegionSettings.DisplayIntervalSeconds) _nextDisplayAt = DateTimeOffset.UtcNow.AddSeconds(RegionSettings.DisplayIntervalSeconds);
        if (previous.DurationSeconds != RegionSettings.DurationSeconds || previous.GuardDurationSeconds != RegionSettings.GuardDurationSeconds)
            foreach (var card in _timed) if (card is not null) card.Expires = card.Displayed.AddSeconds(card.Value.Type == LiveEventType.Guard ? RegionSettings.GuardDurationSeconds : RegionSettings.DurationSeconds);
        ClearViews(); _intakeDue = null; _dirty = true; if (Visible) Process();
    }
    private void ResetContent()
    {
        _wake.Stop(); _detail?.Close(); _detail = null; ClearViews(); Array.Clear(_timed); _queue.Clear(); _list.Clear(); _ranking.Clear(); _board.Clear(); _online = _rank = null; _offset = 0; _intakeDue = null; _nextDisplayAt = default; _dirty = false; ClearHover(); RequestRender();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; ViewModel.RoomHistoryReset -= ResetContent; ViewModel.Dashboard.Cleared -= ResetContent; ResetContent(); _wake.Dispose(); _queue.Dropped -= ViewModel.RecordOverlayDropped; }
        base.Dispose(disposing);
    }
}
