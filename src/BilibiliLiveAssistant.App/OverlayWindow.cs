using System.Drawing;
using BilibiliLiveAssistant.Core;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

public sealed class OverlayWindow : OverlaySurface
{
    private readonly LiveAssistantViewModel _vm;
    private readonly OverlayFrameClock _frames = new();
    private readonly Forms.Timer _intakeTimer = new();
    private readonly List<OverlayItem> _items = new();
    private readonly LiveEventBuffer _incoming = new(64);
    private readonly LiveEventBuffer _pendingItems = new(64);
    private readonly FixedOverlayWindow[] _fixedRegions;
    private readonly StationaryOverlayWindow[] _stationary;
    public bool OverlayVisible => _overlayVisible;
    internal object GetDiagnostics() => new
    {
        visible = _overlayVisible, appearance = DiagnosticAppearance, clock = _frames.GetDiagnostics(), movingItems = _items.Count,
        incoming = _incoming.Count, pending = _pendingItems.Count,
        bitmapBytes = _items.Sum(item => (long)item.Image.Width * item.Image.Height * 4),
        scrolling = _fixedRegions.Select(region => region.GetDiagnostics()).ToArray(),
        stationary = _stationary.Select(region => region.GetDiagnostics()).ToArray(),
    };
    private int _nextTrack;
    private bool _overlayVisible;
    private bool _cleaned;
    private DateTimeOffset _nextDisplayAt;
    private DashboardSettings _messageStyle;
    private MediaPreviewSettings _mediaStyle;

    public OverlayWindow(LiveAssistantViewModel vm) : base(vm, OverlayRegionKind.Danmaku)
    {
        _vm = vm;
        _messageStyle = vm.Settings.Dashboard;
        _mediaStyle = vm.Settings.MediaPreview;
        _fixedRegions = new[] { OverlayRegionKind.Gift, OverlayRegionKind.SuperChat, OverlayRegionKind.Interaction, OverlayRegionKind.Status }
            .Select(kind => new FixedOverlayWindow(vm, kind, _frames)).ToArray();
        _stationary = new[] { OverlayRegionKind.Danmaku, OverlayRegionKind.Gift, OverlayRegionKind.SuperChat, OverlayRegionKind.Status }
            .Select(kind => new StationaryOverlayWindow(vm, kind)).ToArray();
        SetEditorClock(_frames);
        foreach (var region in _fixedRegions) region.SetEditorClock(_frames);
        foreach (var region in _stationary) region.SetEditorClock(_frames);
        _vm.EventBatchReceived += OnEventBatch;
        _vm.RoomHistoryReset += ResetContent;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _intakeTimer.Tick += (_, _) => FlushIncoming();
    }

    public void ActivateOverlay()
    {
        _overlayVisible = true;
        ConfigureFrameRate();
        SyncRegionVisibility();
        if (!Editing && _items.Count > 0) StartAnimation();
    }

    public void HideOverlay()
    {
        _nextDisplayAt = default;
        _overlayVisible = false;
        _frames.Stop(OnFrame);
        _intakeTimer.Stop();
        ClearItems();
        _incoming.Clear();
        _pendingItems.Clear();
        foreach (var region in _fixedRegions) region.HideRegion();
        foreach (var region in _stationary) region.HideRegion();
        _vm.EndOverlayInteraction();
        Hide();
    }

    private void SyncRegionVisibility()
    {
        if (!_overlayVisible) return;
        if ((!Editing && !OverlayRegions.Enabled(_vm.Settings.Display, OverlayRegionKind.Danmaku)) || OverlayRegions.Stationary(_vm.Settings.Display, OverlayRegionKind.Danmaku)) { ResetContent(); Hide(); }
        else if (!Visible) ShowSurface();
        foreach (var region in _fixedRegions)
            if ((Editing || OverlayRegions.Enabled(_vm.Settings.Display, region.RegionKind)) && !OverlayRegions.Stationary(_vm.Settings.Display, region.RegionKind)) region.ShowRegion();
            else region.HideRegion();
        foreach (var region in _stationary)
            if ((Editing || OverlayRegions.Enabled(_vm.Settings.Display, region.RegionKind)) && OverlayRegions.Stationary(_vm.Settings.Display, region.RegionKind)) region.ShowRegion();
            else region.HideRegion();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveAssistantViewModel.OverlayInteractive)) { SyncRegionVisibility(); return; }
        if (e.PropertyName == nameof(LiveAssistantViewModel.Settings))
        {
            if (_messageStyle != _vm.Settings.Dashboard) { _messageStyle = _vm.Settings.Dashboard; ClearItems(); }
            if (_mediaStyle != _vm.Settings.MediaPreview) { _mediaStyle = _vm.Settings.MediaPreview; ClearItems(); }
            SyncRegionVisibility();
            ConfigureFrameRate();
        }
    }

    protected override void DrawContent(Graphics graphics, Rectangle clip)
    {
        foreach (var item in _items)
            if (clip.IntersectsWith(item.Bounds)) OverlayBitmapBlitter.Draw(graphics, item.Image, (float)item.X, item.Y, clip);
    }

    private void OnEventBatch(IReadOnlyList<LiveEvent> batch)
    {
        if (!_overlayVisible || IsDisposed || Editing) return;
        static OverlayRegionKind EventKind(LiveEvent e) => e.Type switch
        { LiveEventType.Danmaku => OverlayRegionKind.Danmaku, LiveEventType.Gift or LiveEventType.Guard => OverlayRegionKind.Gift,
            LiveEventType.SuperChat => OverlayRegionKind.SuperChat, LiveEventType.Enter => OverlayRegionKind.Interaction, _ => OverlayRegionKind.Status };
        foreach (var region in _stationary)
            if (region.Visible) region.EnqueueBatch(batch.Where(e => EventKind(e) == region.RegionKind).ToArray());
        var before = _incoming.DroppedCount;
        foreach (var item in batch)
        {
            if (item.Type == LiveEventType.Danmaku) { if (Visible) _incoming.Enqueue(item); }
            else
            {
                var kind = item.Type switch
                {
                    LiveEventType.Gift or LiveEventType.Guard => OverlayRegionKind.Gift,
                    LiveEventType.SuperChat => OverlayRegionKind.SuperChat,
                    LiveEventType.Enter => OverlayRegionKind.Interaction,
                    _ => OverlayRegionKind.Status,
                };
                foreach (var region in _fixedRegions)
                    if (region.RegionKind == kind && region.Visible) { region.Enqueue(item); break; }
            }
        }
        _vm.RecordOverlayDropped((int)(_incoming.DroppedCount - before));
        if (_incoming.Count > 0 && !_intakeTimer.Enabled)
        {
            _intakeTimer.Interval = RegionSettings.UpdateIntervalMs;
            _intakeTimer.Start();
        }
    }

    private void FlushIncoming()
    {
        _intakeTimer.Stop();
        var before = _pendingItems.DroppedCount;
        foreach (var item in _incoming.Drain()) _pendingItems.Enqueue(item);
        _vm.RecordOverlayDropped((int)(_pendingItems.DroppedCount - before));
        StartAnimation();
    }

    private int TrackHeight => Math.Max(_vm.Settings.Display.TrackHeight,
        Math.Max(RegionSettings.FontSize * 2 + 8, Math.Max(RegionSettings.EmoteSize, RegionSettings.LargeEmoteHeight) + 8));
    private int FindFreeTrack(int span = 1)
    {
        var count = Math.Clamp(_vm.Settings.Display.MaxTracks, 1, Math.Max(1, Math.Min(24, (ClientSize.Height - ContentTop) / TrackHeight)));
        var start = _nextTrack;
        _nextTrack = (_nextTrack + 1) % count;
        for (var offset = 0; offset < count; offset++)
        {
            var candidate = (start + offset) % count;
            if (candidate + span <= count && _items.All(item => item.Track + item.TrackSpan <= candidate || item.Track >= candidate + span || item.X + item.Image.Width < ClientSize.Width - 24)) return candidate;
        }
        return -1;
    }

    private void OnFrame(double elapsed)
    {
        if (_cleaned || !Visible || Editing) { _frames.Stop(OnFrame); return; }
        BeginAnimationFrame();
        try { AdvanceFrame(elapsed); }
        finally { EndAnimationFrame(); }
    }

    private void AdvanceFrame(double elapsed)
    {
        var distance = RegionSettings.ScrollSpeed * elapsed;
        for (var index = _items.Count - 1; index >= 0; index--)
        {
            var item = _items[index];
            var oldBounds = item.Bounds;
            item.X -= distance;
            var dirty = Rectangle.Intersect(ClientRectangle, Rectangle.Union(oldBounds, item.Bounds));
            if (!dirty.IsEmpty) RequestRender(dirty);
            if (item.X + item.Image.Width < 0) { item.Dispose(); _items.RemoveAt(index); }
        }
        var now = DateTimeOffset.UtcNow;
        var track = now >= _nextDisplayAt && _items.Count < RegionSettings.MaxVisible ? FindFreeTrack() : -1;
        var dropped = 0;
        if (track >= 0)
            while (_pendingItems.TryDequeue(out var next))
            {
                // Exclude the deliberate batching delays from stale-message age,
                // otherwise selecting a 2-second update interval drops every message.
                var maxAge = 2 + RegionSettings.DisplayIntervalSeconds + RegionSettings.UpdateIntervalMs / 1000d + _vm.EventBatchIntervalSeconds;
                if ((DateTimeOffset.Now - next!.OccurredAt).TotalSeconds > maxAge) { dropped++; continue; }
                OverlayMediaAttachment? media = null; var span = 1;
                if (next.MediaReferences.Count > 0 && _vm.CanShowOverlayMedia)
                {
                    var height = OverlayMediaAttachment.RequiredHeight(TrackHeight, _vm.Settings.MediaPreview, RegionSettings);
                    var wanted = (int)Math.Ceiling(height / (double)TrackHeight);
                    var mediaTrack = FindFreeTrack(wanted);
                    if (mediaTrack >= 0 && _vm.AcquireOverlayMedia())
                    { track = mediaTrack; span = wanted; media = new(_vm, next.MediaReferences[0], TrackHeight, RegionSettings); }
                }
                var item = new OverlayItem(next, RegionSettings, _vm.Settings.Dashboard, _vm.Images, ClientSize.Width + 20, track, ContentTop + track * TrackHeight, TrackHeight, span, media);
                _items.Add(item);
                _nextDisplayAt = now.AddSeconds(RegionSettings.DisplayIntervalSeconds);
                _ = LoadImagesAsync(item);
                if (media is not null) _ = media.LoadAsync(() =>
                {
                    if (_cleaned || !Visible || !_items.Contains(item)) return;
                    var before = item.Bounds; item.RefreshImage(RegionSettings, _vm.Settings.Dashboard, _vm.Images, TrackHeight); RequestRender(Rectangle.Union(before, item.Bounds));
                });
                break;
            }
        _vm.RecordOverlayDropped(dropped);
        if (_items.Count == 0 && _pendingItems.Count == 0) _frames.Stop(OnFrame);
        else if (_items.Count == 0 && now < _nextDisplayAt)
        {
            _frames.Stop(OnFrame);
            _intakeTimer.Interval = (int)Math.Clamp(Math.Ceiling((_nextDisplayAt - now).TotalMilliseconds), 1, int.MaxValue);
            _intakeTimer.Start();
        }
    }

    private void StartAnimation()
    {
        if (Editing) return;
        if (_items.Count == 0 && _pendingItems.Count == 0) return;
        _frames.Start(OnFrame);
    }

    private void ConfigureFrameRate() => _frames.Configure(_vm.Settings.Display.AnimationFrameRate, Forms.Screen.FromRectangle(Bounds).DeviceName);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Base construction can resize before derived fields are initialized.
        if (_items is null || _vm is null) return;
        var tracks = Math.Clamp(_vm.Settings.Display.MaxTracks, 1, Math.Max(1, (ClientSize.Height - ContentTop) / TrackHeight));
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (item.Media is not null && item.Track + item.TrackSpan > tracks) item.DropMedia(RegionSettings, _vm.Settings.Dashboard, _vm.Images, TrackHeight);
            if (item.Track >= tracks) { item.Dispose(); _items.RemoveAt(i); }
            else item.SetY(ContentTop + item.Track * TrackHeight);
        }
        RequestRender();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_frames is not null && _vm is not null) ConfigureFrameRate();
    }

    protected override void WndProc(ref Forms.Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x007E && _frames is not null && _vm is not null) ConfigureFrameRate();
    }

    private async Task LoadImagesAsync(OverlayItem item)
    {
        var urls = OverlayMessageRenderer.ImageUrls(item.Event, _vm.Settings.Dashboard).Distinct().Take(16)
            .Where(url => _vm.Images.TryGetStaticFrame(url, DecodeLimit(url)) is null).ToArray();
        var cancellation = item.Cancellation.Token;
        try { await Task.WhenAll(urls.Select(LoadOneAsync).Append(LoadAvatarAsync())); }
        catch (OperationCanceledException) { return; }
        async Task LoadAvatarAsync()
        {
            var enriched = await OverlayMessageRenderer.ResolveAvatarAsync(item.Event, _vm.Settings.Dashboard, _vm.Images, cancellation);
            if (_cleaned || !Visible || !_items.Contains(item) || ReferenceEquals(enriched, item.Event)) return;
            item.Event = enriched;
            await LoadOneAsync(enriched.Appearance!.AvatarUrl);
        }
        async Task LoadOneAsync(string url)
        {
            if (await _vm.Images.GetStaticForViewAsync(url, cancellation, DecodeLimit(url)) is null
                || _cleaned || !Visible || !_items.Contains(item)) return;
            var before = item.Bounds;
            item.RefreshImage(RegionSettings, _vm.Settings.Dashboard, _vm.Images, TrackHeight);
            RequestRender(Rectangle.Union(before, item.Bounds));
        }
        int DecodeLimit(string url) => OverlayMessageRenderer.ImageDecodeLimit(item.Event, url, _vm.Settings.Dashboard, RegionSettings);
    }

    protected override void OnRegionSettingsChanged(OverlayRegionSettings previous)
    {
        if (Editing) return;
        if (previous.DisplayIntervalSeconds != RegionSettings.DisplayIntervalSeconds) _nextDisplayAt = DateTimeOffset.UtcNow.AddSeconds(RegionSettings.DisplayIntervalSeconds);
        if (previous.FontFamily != RegionSettings.FontFamily || previous.FontSize != RegionSettings.FontSize
            || previous.EmoteSize != RegionSettings.EmoteSize || previous.LargeEmoteHeight != RegionSettings.LargeEmoteHeight
            || previous.Bold != RegionSettings.Bold || previous.Italic != RegionSettings.Italic
            || previous.Outline != RegionSettings.Outline || previous.Color != RegionSettings.Color || !previous.HasSameEffects(RegionSettings))
        {
            foreach (var item in _items)
                if (item.Media is not null) item.DropMedia(RegionSettings, _vm.Settings.Dashboard, _vm.Images, TrackHeight);
                else item.RefreshImage(RegionSettings, _vm.Settings.Dashboard, _vm.Images, TrackHeight);
            var placed = new List<OverlayItem>();
            foreach (var item in _items.OrderBy(i => i.X))
            {
                foreach (var preceding in placed)
                    if (preceding.Track < item.Track + item.TrackSpan && item.Track < preceding.Track + preceding.TrackSpan)
                        item.X = Math.Max(item.X, preceding.X + preceding.Image.Width + 24);
                placed.Add(item);
            }
        }
        var tracks = Math.Clamp(_vm.Settings.Display.MaxTracks, 1, Math.Max(1, (ClientSize.Height - ContentTop) / TrackHeight));
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (i >= RegionSettings.MaxVisible || _items[i].Track + _items[i].TrackSpan > tracks) { _items[i].Dispose(); _items.RemoveAt(i); }
            else _items[i].SetY(ContentTop + _items[i].Track * TrackHeight);
        }
        _intakeTimer.Interval = RegionSettings.UpdateIntervalMs;
    }

    protected override void OnInteractionChanged() => ResetContent();

    private void ClearItems()
    {
        foreach (var item in _items) item.Dispose();
        _items.Clear();
    }

    private void ResetContent()
    {
        _nextDisplayAt = default;
        _frames.Stop(OnFrame); _intakeTimer.Stop();
        ClearItems(); _incoming.Clear(); _pendingItems.Clear(); RequestRender();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_cleaned)
        {
            _cleaned = true;
            _vm.EventBatchReceived -= OnEventBatch;
            _vm.RoomHistoryReset -= ResetContent;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _frames.Stop(OnFrame); _intakeTimer.Dispose();
            ClearItems();
            foreach (var region in _fixedRegions) region.Dispose();
            foreach (var region in _stationary) region.Dispose();
            _frames.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class OverlayItem : IDisposable
    {
        public OverlayItem(LiveEvent value, OverlayRegionSettings settings, DashboardSettings style, BiliImageCache cache, double x, int track, int y, int height, int span = 1, OverlayMediaAttachment? media = null)
        {
            Event = value; X = x; Track = track; TrackSpan = span; Y = y; Media = media;
            try { Image = CreateImage(value, settings, style, cache, height, media); }
            catch { media?.Dispose(); Cancellation.Dispose(); throw; }
        }
        public OverlayMediaAttachment? Media { get; private set; }
        public int TrackSpan { get; private set; }
        public LiveEvent Event { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public void RefreshImage(OverlayRegionSettings settings, DashboardSettings style, BiliImageCache cache, int height)
        {
            var replacement = CreateImage(Event, settings, style, cache, height, Media);
            Image.Dispose(); Image = replacement;
        }
        private static Bitmap CreateImage(LiveEvent value, OverlayRegionSettings settings, DashboardSettings style, BiliImageCache cache, int height, OverlayMediaAttachment? media)
        {
            var body = OverlayMessageRenderer.Render(value, settings, style, cache, maxHeight: height);
            if (media is null) return body;
            using (body) return media.Compose(body, settings);
        }
        public void DropMedia(OverlayRegionSettings settings, DashboardSettings style, BiliImageCache cache, int height)
        {
            var replacement = CreateImage(Event, settings, style, cache, height, null);
            Media?.Dispose(); Media = null; TrackSpan = 1; Image.Dispose(); Image = replacement;
        }
        public Bitmap Image { get; private set; }
        public double X { get; set; }
        public int Track { get; }
        public int Y { get; private set; }
        public Rectangle Bounds => new((int)Math.Floor(X) - 1, Y - 1, Image.Width + 3, Image.Height + 2);
        public void Dispose() { Cancellation.Cancel(); Cancellation.Dispose(); Media?.Dispose(); Image.Dispose(); }
        public void SetY(int y) => Y = y;
    }
}
