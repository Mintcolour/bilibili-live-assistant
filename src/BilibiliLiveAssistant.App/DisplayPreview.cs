using System.Drawing.Imaging;
using BilibiliLiveAssistant.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace BilibiliLiveAssistant.App;

internal sealed class DisplayPreview : Grid, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Func<bool> _visible;
    private readonly Canvas _canvas = new() { Height = 280, Background = MessageCard.Brush("#161B24") };
    private readonly StackPanel _cards = new() { Spacing = 4, Margin = new(12) };
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };
    private readonly MessageAnimationClock _clock;
    private readonly DispatcherQueueTimer _renderTimer;
    private readonly List<MessageCard> _cardViews = new();
    private readonly List<Visual> _visuals = new();
    private readonly List<UiPixelSource> _pixelSources = new();
    private CancellationTokenSource? _downloads;
    private CancellationTokenSource? _renderCancellation;
    private int _generation;
    private bool _active, _disposed;
    private OverlayRegionKind? _kind;
    private OverlayRegionSettings _region = new();
    private DashboardSettings _style = new();
    private int _frameRate = 60;
    private int _tracks = 8;
    private bool _enabled = true;
    private bool _sampleInitialized;
    private readonly ComboBox _sample = new() { ItemsSource = new[] { "图文消息", "整条图片表情" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Left };
    public DisplayPreview(LiveAssistantViewModel vm, Func<bool> visible)
    {
        _vm = vm; _visible = visible; _clock = new(() => _active && _visible());
        _renderTimer = DispatcherQueue.GetForCurrentThread().CreateTimer(); _renderTimer.Interval = TimeSpan.FromMilliseconds(100); _renderTimer.IsRepeating = false;
        _renderTimer.Tick += async (_, _) =>
        { try { await RenderAsync(); } catch (OperationCanceledException) { } catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_disposed) _hint.Text = $"预览暂不可用：{ex.Message}"; } };
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(_sample); Grid.SetRow(_canvas, 1); Grid.SetRow(_cards, 1); Children.Add(_canvas); Children.Add(_cards); Grid.SetRow(_hint, 2); Children.Add(_hint);
        _sample.SelectionChanged += (_, _) => Schedule();
        _canvas.SizeChanged += (_, _) => Schedule();
    }
    public void Update(OverlayRegionKind? kind, OverlayRegionSettings region, DashboardSettings style, int frameRate, bool enabled, int tracks)
    {
        if (!_sampleInitialized || _kind != kind)
        {
            _sampleInitialized = true;
            var tiers = new[] { "SC · ¥30", "SC · ¥50", "SC · ¥100", "SC · ¥500", "SC · ¥1000", "SC · ¥2000" };
            _sample.ItemsSource = kind == OverlayRegionKind.SuperChat ? tiers : kind is null
                ? new[] { "图文消息", "整条图片表情" }.Concat(tiers).ToArray() : new[] { "图文消息", "整条图片表情" };
            _sample.SelectedIndex = 0;
        }
        _kind = kind; _region = region; _style = style; _frameRate = frameRate; _enabled = enabled; _tracks = tracks; Schedule();
    }
    public void SetActive(bool active)
    {
        if (_disposed) return;
        if (!active || !_visible()) { _active = false; _renderTimer.Stop(); Clear(); _clock.Suspend(); return; }
        if (!_active) { _active = true; Schedule(); }
    }
    private void Schedule() { if (!_disposed && _active && _visible()) { _renderTimer.Stop(); _renderTimer.Start(); } }
    private void Clear()
    {
        _generation++; _downloads?.Cancel(); _downloads?.Dispose(); _downloads = null;
        _renderCancellation?.Cancel(); _renderCancellation?.Dispose(); _renderCancellation = null;
        foreach (var visual in _visuals) { visual.StopAnimation("Translation.X"); visual.StopAnimation("Opacity"); } _visuals.Clear();
        foreach (var card in _cardViews) card.Dispose(); _cardViews.Clear(); _cards.Children.Clear();
        foreach (var image in _canvas.Children.OfType<Image>()) image.Source = null;
        _canvas.Children.Clear();
        foreach (var source in _pixelSources) source.Dispose(); _pixelSources.Clear();
    }
    private async Task RenderAsync()
    {
        if (_disposed || !_active || !_visible() || _canvas.ActualWidth <= 0) return;
        Clear(); var generation = _generation;
        var cancellation = (_renderCancellation = new()).Token;
        var messages = PreviewSamples.For(_kind);
        _sample.Visibility = _kind is null or OverlayRegionKind.Danmaku or OverlayRegionKind.SuperChat ? Visibility.Visible : Visibility.Collapsed;
        if (_sample.Visibility == Visibility.Visible) messages = new[] { messages[Math.Clamp(_sample.SelectedIndex, 0, messages.Length - 1)] };
        if (!_enabled)
        { _canvas.Visibility = Visibility.Visible; _cards.Visibility = Visibility.Collapsed; _hint.Text = "这一类悬浮内容已关闭 · 打开显示开关后预览"; return; }
        if (_kind is null)
        {
            _canvas.Visibility = Visibility.Collapsed; _cards.Visibility = Visibility.Visible;
            foreach (var message in messages)
            {
                var card = new MessageCard(new(message), _vm, _clock, () => _active && _visible(), false, _style);
                _cardViews.Add(card); _cards.Children.Add(card); card.Activate(XamlRoot?.RasterizationScale ?? 1);
            }
            _hint.Text = "概览消息 · 预览使用实际图文组件"; return;
        }
        _canvas.Visibility = Visibility.Visible; _cards.Visibility = Visibility.Collapsed;
        var kind = _kind.Value;
        var stationary = _region.Mode != OverlayDisplayMode.Scrolling;
        var renderStyle = stationary ? _style with { BackgroundColor = _region.BackgroundColor, BackgroundOpacity = _region.BackgroundOpacity, TextColor = _region.Color, TextOpacity = 1 } : _style;
        if (kind == OverlayRegionKind.Status)
        {
            var members = Enumerable.Range(1, _region.RankCount).Select(i => new LiveRankMember(i, i, $"示例观众{i}", PreviewSamples.Appearance with { GuardLevel = i == 1 ? 3 : 0 })).ToArray();
            var metrics = _region.ShowAudienceStats ? new[] { new LiveAudienceMetric(LiveAudienceMetricKind.EnergyMembers, "620"), new LiveAudienceMetric(LiveAudienceMetricKind.Watched, "1.2万") } : Array.Empty<LiveAudienceMetric>();
            messages = OverlayBulletinRenderer.CreateParts(metrics,
                members, true, OverlayRegions.ForRendering(_region), _style, _vm.Images, 160).Take(1).ToArray();
            if (stationary) messages = new[] { new LiveEvent(LiveEventType.Rank, "", "", _region.Color, Metadata: new Dictionary<string, object?> { ["room_bulletin"] = new LiveRoomBulletin(metrics, [], false) }) }
                .Concat(members.Select(m => OverlayBulletinRenderer.MemberEvent(m, _region.Color))).ToArray();
        }
        if (kind == OverlayRegionKind.Interaction) messages = messages.Take(Math.Min(3, _region.MaxVisible)).ToArray();
        if (stationary && kind != OverlayRegionKind.Status) messages = Enumerable.Range(0, _region.Mode == OverlayDisplayMode.Grid ? 4 : Math.Min(3, _region.MaxVisible)).Select(i => messages[i % messages.Length]).ToArray();
        var y = 12d; var slot = 0; var columns = _region.Mode == OverlayDisplayMode.Grid ? Math.Max(1, Math.Min(_region.CardColumns, (int)_canvas.ActualWidth / 200)) : 1;
        var cellWidth = Math.Max(140, ((int)_canvas.ActualWidth - 24 - (columns - 1) * 8) / columns);
        foreach (var message in messages)
        {
            var region = OverlayRegions.ForRendering(stationary && kind == OverlayRegionKind.Gift ? _region with { Bold = true } : _region);
            using var rendered = OverlayMessageRenderer.Render(message, region, renderStyle, _vm.Images,
                width: stationary ? cellWidth : kind == OverlayRegionKind.Interaction ? (int)_canvas.ActualWidth - 24 : 0,
                maxHeight: stationary ? kind == OverlayRegionKind.Status ? OverlayBulletinRenderer.PanelRowHeight(region, _style) : Math.Max(region.CardHeight, region.FontSize * 3 + 24) : Math.Max(160, _region.LargeEmoteHeight + 16),
                card: stationary && kind != OverlayRegionKind.Status, background: !stationary);
            using var bitmap = new System.Drawing.Bitmap(rendered.Width, stationary && kind != OverlayRegionKind.Status ? Math.Max(region.CardHeight, region.FontSize * 3 + 24) : rendered.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                if (stationary) OverlayMessageRenderer.DrawBackground(graphics, bitmap.Size, region, OverlayTextRenderer.Color(region.Color));
                graphics.DrawImageUnscaled(rendered, 0, 0);
            }
            var source = await UiPixelSource.CreateAsync(BiliPixelFrame.FromDrawingBitmap(bitmap), bitmap.Width, false, 256, cancellation);
            if (_disposed || generation != _generation || !_active || !_visible()) { source.Dispose(); return; }
            _pixelSources.Add(source);
            var image = new Image { Source = source.Source, Width = bitmap.Width, Height = bitmap.Height, Stretch = Stretch.Fill, Opacity = _region.Opacity };
            if (stationary && columns > 1) y = 12 + slot / columns * (Math.Max(region.CardHeight, region.FontSize * 3 + 24) + 8);
            Canvas.SetTop(image, y); Canvas.SetLeft(image, stationary ? 12 + slot % columns * (cellWidth + 8) : 0); _canvas.Children.Add(image);
            if (columns == 1) y += (stationary && kind != OverlayRegionKind.Status ? Math.Max(region.CardHeight, region.FontSize * 3 + 24) : stationary ? OverlayBulletinRenderer.PanelRowHeight(region, _style) : bitmap.Height) + (stationary && kind == OverlayRegionKind.Status ? OverlayBulletinRenderer.PanelGap : 8);
            slot++;
            ElementCompositionPreview.SetIsTranslationEnabled(image, true);
            var visual = ElementCompositionPreview.GetElementVisual(image); _visuals.Add(visual);
            if (!stationary && kind != OverlayRegionKind.Interaction)
            {
                var animation = visual.Compositor.CreateScalarKeyFrameAnimation(); animation.InsertKeyFrame(0, (float)_canvas.ActualWidth);
                animation.InsertKeyFrame(1, -(float)bitmap.Width, visual.Compositor.CreateLinearEasingFunction());
                animation.Duration = TimeSpan.FromSeconds((_canvas.ActualWidth + bitmap.Width) / Math.Max(30, _region.ScrollSpeed));
                animation.IterationBehavior = AnimationIterationBehavior.Forever; visual.StartAnimation("Translation.X", animation); animation.Dispose();
            }
            else if (!stationary)
            {
                Canvas.SetLeft(image, 12);
                var duration = LiveInteractions.IsFollow(message) ? _region.FollowDurationSeconds : _region.DurationSeconds;
                var animation = visual.Compositor.CreateScalarKeyFrameAnimation(); animation.InsertKeyFrame(0, (float)_region.Opacity);
                animation.InsertKeyFrame((float)(duration / (duration + 1)), (float)_region.Opacity);
                animation.InsertKeyFrame(1, 0, visual.Compositor.CreateStepEasingFunction()); animation.Duration = TimeSpan.FromSeconds(duration + 1);
                animation.IterationBehavior = AnimationIterationBehavior.Forever; visual.StartAnimation("Opacity", animation); animation.Dispose();
            }
        }
        var effective = OverlayRegions.ForRendering(_region);
        _canvas.Height = Math.Max(280, y + (stationary && columns > 1 ? Math.Max(effective.CardHeight, effective.FontSize * 3 + 24) + 12 : 0)); _canvas.Clip = new RectangleGeometry { Rect = new(0, 0, _canvas.ActualWidth, _canvas.Height) };
        _hint.Text = stationary ? $"{OverlayRegions.ModeTitle(_region.Mode)} · 高度 {_region.CardHeight} px" + (_region.Mode == OverlayDisplayMode.Cards ? $" · 每条 {_region.DurationSeconds:0} 秒 / 上舰 {_region.GuardDurationSeconds:0} 秒 · 最多 {_region.MaxVisible} 张" : " · 无持续动画")
            : kind == OverlayRegionKind.Interaction ? $"进房／点赞 {_region.DurationSeconds:0.#} 秒 · 关注 {_region.FollowDurationSeconds:0.#} 秒 · 最多 {_region.MaxVisible} 条，关注优先"
            : $"{_region.ScrollSpeed} px/s · {(_frameRate == 0 ? "跟随屏幕" : $"实际悬浮 {_frameRate} FPS")}{(kind == OverlayRegionKind.Danmaku ? $" · 最多 {_tracks} 轨道 / {_region.MaxVisible} 条" : "")} · 预览展示样式与速度";
        var download = new CancellationTokenSource(); _downloads = download;
        var urls = messages.SelectMany(message => OverlayMessageRenderer.ImageUrls(message, _style)
            .Select(url => (Url: url, Role: OverlayMessageRenderer.ImageRole(message, url)))).Distinct().Take(16)
            .Where(request => _vm.Images.TryGetStaticFrame(request.Url, role: request.Role) is null).ToArray();
        if (urls.Length > 0) _ = LoadAsync(urls, download.Token, generation);
    }
    private async Task LoadAsync((string Url, BiliImageRole Role)[] urls, CancellationToken token, int generation)
    {
        try
        {
            var updated = false;
            foreach (var request in urls) { if (await _vm.Images.GetStaticForViewAsync(request.Url, token, role: request.Role) is not null) updated = true; }
            if (updated && !_disposed && generation == _generation && _active && _visible()) Schedule();
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _renderTimer.Stop(); Clear(); _clock.Dispose(); }
}

internal static class PreviewSamples
{
    internal static readonly LiveMessageAppearance Appearance = new()
    {
        AvatarUrl = "https://i0.hdslb.com/bfs/face/109b787c5f113c4a3c7415bb9bf6b82bc33bc4e2.jpg", MedalName = "示例牌", MedalLevel = 21, MedalColor = "#4388BC", GuardLevel = 3,
        Emotes = new[] { new LiveEmote("[热]", "https://i0.hdslb.com/bfs/live/6df760280b17a6cbac8c1874d357298f982ba4cf.png", 20, 20) },
    };
    internal static LiveEvent[] For(OverlayRegionKind? kind)
    {
        if (kind == OverlayRegionKind.SuperChat) return SuperChats();
        var type = kind switch { OverlayRegionKind.Gift => LiveEventType.Gift, OverlayRegionKind.SuperChat => LiveEventType.SuperChat,
            OverlayRegionKind.Interaction => LiveEventType.Enter, _ => LiveEventType.Danmaku };
        if (kind == OverlayRegionKind.Interaction) return [new(type, "关注观众", "关注了主播", "#FF8AB3", 1, Appearance: Appearance) { HistoryFields = new(InteractionKind: 2) },
            new(type, "点赞观众", "为主播点赞了", "#FFAA78", 2, Appearance: Appearance) { HistoryFields = new(InteractionKind: 6) },
            new(type, "进房观众", "进入了直播间", "#71C6A6", 3, Appearance: Appearance)];
        var messages = new[] { new LiveEvent(type, "示例观众", kind == OverlayRegionKind.Gift ? "赠送 小心心 × 3" : "这是一条带图片表情的弹幕 [热] 😊 👍🏽 🇨🇳 👨‍👩‍👧‍👦", "#FFFFFF", 1, Appearance: Appearance),
            new LiveEvent(LiveEventType.Danmaku, "大表情示例", "[图片表情]", "#FFFFFF", 2, Appearance: Appearance with
            { FullEmote = new("[图片表情]", "https://i0.hdslb.com/bfs/live/a98e35996545509188fe4d24bd1a56518ea5af48.png", 183, 60) }) }.Take(kind == OverlayRegionKind.Gift ? 1 : 2).ToArray();
        return kind is null ? messages.Concat(SuperChats()).ToArray() : messages;
    }
    private static LiveEvent[] SuperChats() => new[] { 30, 50, 100, 500, 1000, 2000 }.Select(amount =>
        new LiveEvent(LiveEventType.SuperChat, "分档示例观众", $"¥{amount} 感谢陪伴 [热] 😊 👍🏽 👨‍👩‍👧‍👦", "#FFFFFF", 1, Appearance: Appearance)
        { HistoryFields = new(AmountMilliYuan: amount * 1000L, RawBody: "感谢陪伴 [热] 😊 👍🏽 👨‍👩‍👧‍👦") }).ToArray();
}
