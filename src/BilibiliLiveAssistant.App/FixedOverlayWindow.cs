using System.Diagnostics;
using System.Drawing;
using BilibiliLiveAssistant.Core;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

// Independent bounded playback. Only entry notifications remain stationary.
internal sealed class FixedOverlayWindow : OverlaySurface
{
    private const int QueueLimit = 64;
    private readonly Forms.Timer _wake = new();
    private readonly OverlayFrameClock _frames;
    private readonly List<Pending> _waiting = new();
    private readonly List<Sprite> _sprites = new();
    private Queue<LiveEvent>? _scParts;
    private Queue<LiveEvent>? _bulletinParts;
    private LiveEvent? _currentSc;
    private LiveEvent? _online, _rank;
    private long _broadcastAt;
    private long _nextDisplayAt;
    private bool _cleaned;
    private DashboardSettings _style;
    private MediaPreviewSettings _mediaStyle;
    public OverlayRegionKind RegionKind => Kind;
    internal object GetDiagnostics() => new { kind = Kind.ToString(), visible = Visible, appearance = DiagnosticAppearance, sprites = _sprites.Count,
        waiting = _waiting.Count, scParts = _scParts?.Count ?? 0, bulletinParts = _bulletinParts?.Count ?? 0,
        bitmapBytes = _sprites.Sum(sprite => (long)sprite.Image.Width * sprite.Image.Height * 4) };

    public FixedOverlayWindow(LiveAssistantViewModel vm, OverlayRegionKind kind, OverlayFrameClock frames) : base(vm, kind)
    {
        _frames = frames;
        _style = vm.Settings.Dashboard;
        _mediaStyle = vm.Settings.MediaPreview;
        _wake.Tick += (_, _) => Process();
        vm.RoomHistoryReset += ResetContent;
    }

    private static long Now => Stopwatch.GetTimestamp();
    private static long After(long now, double seconds) => now + (long)(seconds * Stopwatch.Frequency);
    private static double Age(long stamp, long now) => (now - stamp) / (double)Stopwatch.Frequency;
    private int RowHeight => Math.Max(RegionSettings.FontSize * 2 + 12,
        Kind is OverlayRegionKind.SuperChat ? Math.Max(RegionSettings.EmoteSize, RegionSettings.LargeEmoteHeight) + 8 : RegionSettings.EmoteSize + 8);
    private int Lanes => Kind is OverlayRegionKind.SuperChat or OverlayRegionKind.Status ? 1
        : Math.Clamp((ClientSize.Height - ContentTop) / RowHeight, 1,
            Kind == OverlayRegionKind.Interaction ? Math.Min(3, RegionSettings.MaxVisible) : Math.Min(10, RegionSettings.MaxVisible));

    public void ShowRegion()
    {
        if (Visible) return;
        ShowSurface();
        if (Editing) return;
        if (Kind == OverlayRegionKind.Status)
        {
            _online = ViewModel.LatestOnline; _rank = ViewModel.LatestRank;
            Process();
        }
    }

    public void HideRegion() { ResetContent(); Hide(); }

    private void ResetContent()
    {
        _wake.Stop(); _frames.Stop(OnFrame);
        _waiting.Clear(); _scParts = null; _bulletinParts = null; _currentSc = null; _online = null; _rank = null; _broadcastAt = 0; _nextDisplayAt = 0;
        ClearSprites(); RequestRender();
    }

    public void Enqueue(LiveEvent value)
    {
        if (!Visible || IsDisposed || Editing) return;
        var now = Now;
        if (Kind == OverlayRegionKind.Status)
        {
            if (value.Type == LiveEventType.Online) _online = value.Extra.TryGetValue("sample", out var sample) && sample is true
                ? value : ViewModel.LatestOnline ?? value;
            else if (value.Type == LiveEventType.Rank) _rank = value;
            if (_broadcastAt == 0) _broadcastAt = After(now, RegionSettings.UpdateIntervalMs / 1000d);
            ScheduleWake(); return;
        }
        if (Kind == OverlayRegionKind.Interaction)
        {
            var expiredEntries = ExpireEntries(now);
            while (_sprites.Count >= Lanes)
            {
                var replace = _sprites.FindIndex(s => !LiveInteractions.IsFollow(s.Event));
                // Entry bursts cannot evict a follow before its configured expiry.
                if (replace < 0 && !LiveInteractions.IsFollow(value))
                {
                    if (expiredEntries) { PositionEntries(); RequestRender(); ScheduleWake(); }
                    return;
                }
                if (replace < 0) replace = 0; // Follow bursts still keep the newest within the same bound.
                _sprites[replace].Dispose(); _sprites.RemoveAt(replace);
            }
            var sprite = AddSprite(value, 0, 8);
            sprite.CreatedAt = now;
            sprite.ExpiresAt = After(now, InteractionDuration(value));
            PositionEntries(); RequestRender(); ScheduleWake(); return;
        }
        if (Kind == OverlayRegionKind.Gift && RegionSettings.MergeSeconds > 0)
        {
            // The deadline is anchored to the first receipt, never extended by later gifts.
            var match = _waiting.Find(p => p.MergeUntil > now && GiftAggregation.CanMerge(p.Event, value));
            if (match is not null) { match.Merge(value); ScheduleWake(); return; }
        }
        if (_waiting.Count == QueueLimit)
        {
            // SC keeps receipt order; reject excess arrivals instead of evicting earlier SC.
            if (Kind == OverlayRegionKind.SuperChat) { ViewModel.RecordOverlayDropped(1); return; }
            _waiting.RemoveAt(0); ViewModel.RecordOverlayDropped(1);
        }
        var delay = Kind == OverlayRegionKind.Gift ? RegionSettings.MergeSeconds : 0;
        _waiting.Add(new Pending(value, now, After(now, Math.Max(delay, RegionSettings.UpdateIntervalMs / 1000d)), After(now, delay)));
        ScheduleWake();
    }

    private void Process()
    {
        _wake.Stop();
        if (!Visible || _cleaned || Editing) return;
        var now = Now;
        if (Kind == OverlayRegionKind.Interaction)
        {
            if (ExpireEntries(now)) { PositionEntries(); RequestRender(); }
        }
        else if (Kind == OverlayRegionKind.Status)
        {
            if ((_online is not null || _rank is not null) && (_broadcastAt == 0 || now >= _broadcastAt))
            {
                _broadcastAt = After(now, RegionSettings.BroadcastIntervalSeconds);
                // Skip the tick if the previous bulletin is still crossing this region.
                if (_sprites.Count == 0 && _bulletinParts is not { Count: > 0 })
                    _bulletinParts = new Queue<LiveEvent>(CreateBulletinParts());
            }
            if (_sprites.Count == 0 && _bulletinParts is { Count: > 0 }) AddSprite(_bulletinParts.Dequeue(), 0, ClientSize.Width + 12);
        }
        else
        {
            var maxAge = Kind == OverlayRegionKind.SuperChat ? 300 : Math.Max(15, Math.Max(RegionSettings.MergeSeconds + 10, RegionSettings.DisplayIntervalSeconds + 2));
            var expired = _waiting.RemoveAll(p => Age(p.CreatedAt, now) >= maxAge);
            ViewModel.RecordOverlayDropped(expired);
            while (_sprites.Count < Lanes)
            {
                if (_scParts is not { Count: > 0 } && now < _nextDisplayAt) break;
                LiveEvent next;
                if (_scParts is { Count: > 0 }) next = _scParts.Dequeue();
                else
                {
                    _scParts = null;
                    _currentSc = null;
                    var index = Kind == OverlayRegionKind.Gift ? _waiting.FindLastIndex(p => p.ReadyAt <= now)
                        : _waiting.FindIndex(p => p.ReadyAt <= now);
                    if (index < 0) break;
                    var pending = _waiting[index]; _waiting.RemoveAt(index);
                    next = pending.Combined();
                    if (Kind == OverlayRegionKind.SuperChat)
                    {
                        _currentSc = next;
                        _scParts = new Queue<LiveEvent>(OverlayMessageRenderer.ScrollParts(next, RegionSettings, _style, ViewModel.Images));
                        next = _scParts.Dequeue();
                    }
                }
                var lane = Enumerable.Range(0, Lanes).First(i => _sprites.All(s => s.Lane != i));
                AddSprite(next, lane, ClientSize.Width + 12);
                if (_scParts is not { Count: > 0 }) _nextDisplayAt = After(now, RegionSettings.DisplayIntervalSeconds);
            }
        }
        if (Kind != OverlayRegionKind.Interaction && _sprites.Count > 0) _frames.Start(OnFrame);
        ScheduleWake();
    }

    private IReadOnlyList<LiveEvent> CreateBulletinParts()
    {
        var metrics = OverlayBulletinRenderer.AudienceMetrics(_rank, _online, RegionSettings.ShowAudienceStats);
        var members = _rank is null ? Array.Empty<LiveRankMember>() : ViewModel.GetRankMembers(_rank, RegionSettings.RankCount);
        return OverlayBulletinRenderer.CreateParts(metrics, members, _rank is not null, RegionSettings, _style, ViewModel.Images, RowHeight);
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
        var removed = false;
        for (var i = _sprites.Count - 1; i >= 0; i--)
        {
            var sprite = _sprites[i]; var before = sprite.Bounds; sprite.X -= distance;
            RequestRender(Rectangle.Union(before, sprite.Bounds));
            if (sprite.X + sprite.Image.Width < 0) { sprite.Dispose(); _sprites.RemoveAt(i); removed = true; }
        }
        if (_sprites.Count == 0) _frames.Stop(OnFrame);
        if (removed) Process();
    }

    private bool ExpireEntries(long now)
    {
        var changed = false;
        for (var i = _sprites.Count - 1; i >= 0; i--)
            if (_sprites[i].ExpiresAt <= now) { _sprites[i].Dispose(); _sprites.RemoveAt(i); changed = true; }
        return changed;
    }

    private double InteractionDuration(LiveEvent value) => LiveInteractions.IsFollow(value) ? RegionSettings.FollowDurationSeconds : RegionSettings.DurationSeconds;

    private void PositionEntries()
    {
        var row = 0;
        for (var pass = 0; pass < 2; pass++)
            foreach (var sprite in _sprites)
                if (LiveInteractions.IsFollow(sprite.Event) == (pass == 0)) sprite.Y = ContentTop + row++ * RowHeight;
    }

    private Sprite AddSprite(LiveEvent value, int lane, double x)
    {
        OverlayMediaAttachment? media = null;
        var firstPart = !value.Extra.TryGetValue("scroll_part", out var part) || part is string text && (text.Length == 0 || text.StartsWith("1/", StringComparison.Ordinal));
        if (Kind == OverlayRegionKind.SuperChat && firstPart && value.MediaReferences.Count > 0 && ViewModel.CanShowOverlayMedia
            && OverlayMediaAttachment.RequiredHeight(RowHeight, ViewModel.Settings.MediaPreview, RegionSettings) <= ClientSize.Height - ContentTop
            && ViewModel.AcquireOverlayMedia()) media = new(ViewModel, value.MediaReferences[0], RowHeight, RegionSettings);
        Bitmap image;
        try
        {
            var body = OverlayMessageRenderer.Render(value, RegionSettings, _style, ViewModel.Images,
                width: Kind == OverlayRegionKind.Interaction ? ClientSize.Width - 16 : 0, maxHeight: RowHeight);
            if (media is null) image = body;
            else using (body) image = media.Compose(body, RegionSettings);
        }
        catch { media?.Dispose(); throw; }
        var sprite = new Sprite(value, lane, x, ContentTop + lane * RowHeight, image, media);
        _sprites.Add(sprite); _ = LoadImagesAsync(sprite);
        if (media is not null) _ = media.LoadAsync(() =>
        {
            if (_cleaned || !Visible || !_sprites.Contains(sprite)) return;
            var before = sprite.Bounds;
            using var updated = OverlayMessageRenderer.Render(sprite.Event, RegionSettings, _style, ViewModel.Images, maxHeight: RowHeight);
            var replacement = media.Compose(updated, RegionSettings);
            sprite.Image.Dispose(); sprite.Image = replacement; RequestRender(Rectangle.Union(before, sprite.Bounds));
        });
        RequestRender(sprite.Bounds); return sprite;
    }

    private async Task LoadImagesAsync(Sprite sprite)
    {
        if (sprite.ImagesLoading) return;
        sprite.ImagesLoading = true;
        try
        {
            var cancellation = sprite.Cancellation.Token;
            var urls = OverlayMessageRenderer.ImageUrls(sprite.Event, _style).Distinct().Take(Kind == OverlayRegionKind.Status ? 32 : 16)
                .Where(url => ViewModel.Images.TryGetFrame(url, DecodeLimit(url)) is null).ToArray();
            if (urls.Length == 0) return;
            await Task.WhenAll(urls.Select(LoadOneAsync));
            async Task LoadOneAsync(string url)
            {
                if (await ViewModel.Images.GetForViewAsync(url, false, cancellation, DecodeLimit(url)) is null
                    || _cleaned || !Visible || !_sprites.Contains(sprite)) return;
                // A slow avatar must not hold back an emote that is already available.
                var before = sprite.Bounds;
                var body = OverlayMessageRenderer.Render(sprite.Event, RegionSettings, _style, ViewModel.Images,
                    width: Kind == OverlayRegionKind.Interaction ? ClientSize.Width - 16 : 0, maxHeight: RowHeight);
                var image = body;
                if (sprite.Media is { } media) { using (body) image = media.Compose(body, RegionSettings); }
                sprite.Image.Dispose(); sprite.Image = image;
                RequestRender(Rectangle.Union(before, sprite.Bounds));
            }
            int DecodeLimit(string url) => OverlayMessageRenderer.ImageDecodeLimit(sprite.Event, url, _style, RegionSettings);
        }
        catch (OperationCanceledException) { }
        finally { sprite.ImagesLoading = false; }
    }

    private void ScheduleWake()
    {
        _wake.Stop();
        if (!Visible || _cleaned) return;
        var now = Now; var due = long.MaxValue;
        if (Kind == OverlayRegionKind.Status && (_online is not null || _rank is not null)) due = _broadcastAt;
        if (Kind == OverlayRegionKind.Interaction)
            foreach (var sprite in _sprites) due = Math.Min(due, sprite.ExpiresAt);
        foreach (var pending in _waiting)
        {
            if (_sprites.Count < Lanes) due = Math.Min(due, Math.Max(pending.ReadyAt, _nextDisplayAt));
            due = Math.Min(due, After(pending.CreatedAt, Kind == OverlayRegionKind.SuperChat ? 300 : Math.Max(15, Math.Max(RegionSettings.MergeSeconds + 10, RegionSettings.DisplayIntervalSeconds + 2))));
        }
        if (due == long.MaxValue) return;
        _wake.Interval = (int)Math.Clamp(Math.Ceiling(Age(now, due) * 1000), 1, int.MaxValue); _wake.Start();
    }

    protected override void DrawContent(Graphics graphics, Rectangle clip)
    {
        foreach (var sprite in _sprites)
        {
            if (!clip.IntersectsWith(sprite.Bounds)) continue;
            if (Kind == OverlayRegionKind.Interaction) graphics.DrawImageUnscaled(sprite.Image, (int)sprite.X, sprite.Y);
            else OverlayBitmapBlitter.Draw(graphics, sprite.Image, (float)sprite.X, sprite.Y, clip);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Base construction can resize before derived fields have been initialized.
        if (_sprites is null) return;
        if (Kind == OverlayRegionKind.Interaction) RefreshEntries();
        else PositionScrolling();
        RequestRender();
    }

    private void PositionScrolling()
    {
        for (var i = _sprites.Count - 1; i >= 0; i--)
            if (_sprites[i].Lane >= Lanes) { _sprites[i].Dispose(); _sprites.RemoveAt(i); }
            else
            {
                var sprite = _sprites[i]; sprite.Y = ContentTop + sprite.Lane * RowHeight;
                if (sprite.Media is not null && sprite.Y + sprite.Image.Height > ClientSize.Height)
                {
                    var body = OverlayMessageRenderer.Render(sprite.Event, RegionSettings, _style, ViewModel.Images, maxHeight: RowHeight);
                    sprite.Media.Dispose(); sprite.Media = null; sprite.Image.Dispose(); sprite.Image = body;
                }
            }
    }

    private void RefreshEntries()
    {
        while (_sprites.Count > Lanes)
        {
            var index = _sprites.FindIndex(s => !LiveInteractions.IsFollow(s.Event)); if (index < 0) index = 0;
            _sprites[index].Dispose(); _sprites.RemoveAt(index);
        }
        PositionEntries();
        foreach (var sprite in _sprites)
        {
            var image = OverlayMessageRenderer.Render(sprite.Event, RegionSettings, _style, ViewModel.Images,
                width: ClientSize.Width - 16, maxHeight: RowHeight);
            sprite.Image.Dispose(); sprite.Image = image;
            _ = LoadImagesAsync(sprite);
        }
    }

    protected override void OnRegionSettingsChanged(OverlayRegionSettings previous)
    {
        if (Editing) return;
        if (previous.DisplayIntervalSeconds != RegionSettings.DisplayIntervalSeconds) _nextDisplayAt = After(Now, RegionSettings.DisplayIntervalSeconds);
        var appearanceChanged = previous.FontFamily != RegionSettings.FontFamily || previous.FontSize != RegionSettings.FontSize
            || previous.EmoteSize != RegionSettings.EmoteSize || previous.LargeEmoteHeight != RegionSettings.LargeEmoteHeight
            || previous.Color != RegionSettings.Color || previous.Bold != RegionSettings.Bold || previous.Italic != RegionSettings.Italic
            || previous.Outline != RegionSettings.Outline || !previous.HasSameEffects(RegionSettings) || _style != ViewModel.Settings.Dashboard || _mediaStyle != ViewModel.Settings.MediaPreview;
        _style = ViewModel.Settings.Dashboard;
        _mediaStyle = ViewModel.Settings.MediaPreview;
        if (Kind != OverlayRegionKind.Interaction) PositionScrolling();
        if (Kind == OverlayRegionKind.Interaction)
        {
            foreach (var sprite in _sprites) sprite.ExpiresAt = After(sprite.CreatedAt, InteractionDuration(sprite.Event));
            RefreshEntries();
        }
        else if (appearanceChanged)
        {
            foreach (var sprite in _sprites)
            {
                var body = OverlayMessageRenderer.Render(sprite.Event, RegionSettings, _style, ViewModel.Images, maxHeight: RowHeight);
                sprite.Media?.Dispose(); sprite.Media = null; sprite.Image.Dispose(); sprite.Image = body;
            }
            PositionScrolling();
        }
        if (Kind == OverlayRegionKind.Status && previous.BroadcastIntervalSeconds != RegionSettings.BroadcastIntervalSeconds)
            _broadcastAt = After(Now, RegionSettings.BroadcastIntervalSeconds);
        if (Visible) Process();
    }

    protected override void OnInteractionChanged()
    {
        ResetContent();
        if (!Editing && OverlayRegions.Enabled(ViewModel.Settings.Display, Kind) && Kind == OverlayRegionKind.Status)
        { _online = ViewModel.LatestOnline; _rank = ViewModel.LatestRank; if (Visible) Process(); }
    }

    private void ClearSprites() { foreach (var sprite in _sprites) sprite.Dispose(); _sprites.Clear(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_cleaned)
        { _cleaned = true; ViewModel.RoomHistoryReset -= ResetContent; ResetContent(); _wake.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class Pending
    {
        public LiveEvent Event { get; private set; }
        public long CreatedAt { get; }
        public long ReadyAt { get; }
        public long MergeUntil { get; }
        public Pending(LiveEvent value, long now, long ready, long mergeUntil)
        { Event = value; CreatedAt = now; ReadyAt = ready; MergeUntil = mergeUntil; }
        public void Merge(LiveEvent value)
        { Event = GiftAggregation.Combine(Event, value); }
        public LiveEvent Combined() => Event;
    }
    private sealed class Sprite : IDisposable
    {
        public LiveEvent Event { get; }
        public int Lane { get; }
        public double X;
        public int Y;
        public long CreatedAt, ExpiresAt;
        public Bitmap Image;
        public bool ImagesLoading;
        public OverlayMediaAttachment? Media { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Sprite(LiveEvent value, int lane, double x, int y, Bitmap image, OverlayMediaAttachment? media = null) { Event = value; Lane = lane; X = x; Y = y; Image = image; Media = media; }
        public Rectangle Bounds => new((int)Math.Floor(X) - 1, Y - 1, Image.Width + 3, Image.Height + 2);
        public void Dispose() { Cancellation.Cancel(); Cancellation.Dispose(); Media?.Dispose(); Image.Dispose(); }
    }
}
