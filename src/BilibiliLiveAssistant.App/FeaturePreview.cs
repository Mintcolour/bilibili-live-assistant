using BilibiliLiveAssistant.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;

namespace BilibiliLiveAssistant.App;

/// <summary>Offline visual examples; drafts never query media providers.</summary>
internal sealed class FeaturePreview : StackPanel, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Func<bool> _visible;
    private readonly MessageAnimationClock _clock;
    private readonly List<MessageCard> _cards = new();
    private readonly List<UiPixelSource> _pixelSources = new();
    private readonly DispatcherQueueTimer _renderTimer;
    private readonly DispatcherQueueTimer _feed;
    private readonly List<(StackPanel Host, TextBlock Count)> _slots = new();
    private Visual? _mediaVisual;
    private int _step;
    private readonly ComboBox _provider = new() { ItemsSource = new[] { "B站视频示例", "Steam 游戏示例" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Left };
    private MediaPreviewTile? _tile;
    private bool _active, _disposed;
    private int _generation;
    private CancellationTokenSource? _renderCancellation;
    private string _kind = "media";
    private MediaPreviewSettings _media = new();
    private DanmakuCardSettings _settings = new();
    private DashboardSettings _style = new();
    private OverlayRegionSettings _region = new();
    public FeaturePreview(LiveAssistantViewModel vm, Func<bool> visible)
    {
        _vm = vm; _visible = visible; _clock = new(() => _active && _visible()); Spacing = 12;
        _renderTimer = DispatcherQueue.GetForCurrentThread().CreateTimer(); _renderTimer.Interval = TimeSpan.FromMilliseconds(100); _renderTimer.IsRepeating = false;
        _renderTimer.Tick += (_, _) => Render();
        _feed = DispatcherQueue.GetForCurrentThread().CreateTimer(); _feed.Interval = TimeSpan.FromSeconds(1.8); _feed.IsRepeating = false;
        _feed.Tick += (_, _) =>
        {
            if (_disposed || !_active || !_visible() || _slots.Count == 0) return;
            try
            {
                _step++; var index = (_step / 2) % _slots.Count; var slot = _slots[index];
                if (!_settings.Enabled)
                {
                    var old = _cards[0]; Children.Remove(_slots[0].Host); old.Dispose(); _cards.RemoveAt(0); _slots.RemoveAt(0);
                    var card = NewCard(_step + 2); _cards.Add(card);
                    var label = new TextBlock { Text = $"{DateTime.Now:HH:mm:ss}   新消息", FontSize = 11, Opacity = .65 };
                    var host = new StackPanel { Children = { label, card } }; _slots.Add((host, label)); Children.Insert(Children.Count - 1, host);
                }
                else if (_step % 2 == 1) slot.Count.Text = $"×{2 + _step / _slots.Count}   {DateTime.Now:HH:mm:ss}   重复消息更新";
                else
                {
                    var old = _cards[index]; slot.Host.Children.Remove(old); old.Dispose();
                    var card = NewCard(_step); _cards[index] = card; slot.Host.Children.Add(card);
                    slot.Count.Text = $"×1   {DateTime.Now:HH:mm:ss}   新消息";
                }
                _feed.Start();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { RuntimeErrorLog.Record("feature-preview-feed", ex); }
        };
        _provider.SelectionChanged += (_, _) => Schedule();
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) Schedule(); };
    }
    public void Update(string kind, MediaPreviewSettings media, DanmakuCardSettings settings, DashboardSettings style, OverlayRegionSettings region)
    { _kind = kind; _media = media; _settings = settings; _style = style; _region = region; Schedule(); }
    private void Schedule() { if (!_disposed && _active && _visible()) { _renderTimer.Stop(); _renderTimer.Start(); } }
    public void SetActive(bool active) { if (_disposed || _active == active) return; _active = active; if (active) Schedule(); else { _renderTimer.Stop(); Clear(); } }
    private void Clear()
    {
        _feed.Stop(); _step = 0; _slots.Clear();
        _mediaVisual?.StopAnimation("Translation.X"); _mediaVisual = null;
        _generation++; _renderCancellation?.Cancel(); _renderCancellation?.Dispose(); _renderCancellation = null;
        _tile?.Dispose(); _tile = null; foreach (var card in _cards) card.Dispose(); _cards.Clear();
        foreach (var canvas in Children.OfType<Canvas>()) foreach (var image in canvas.Children.OfType<Image>()) image.Source = null;
        Children.Clear(); foreach (var source in _pixelSources) source.Dispose(); _pixelSources.Clear(); _clock.Suspend();
    }
    private async void Render()
    {
        if (_disposed || !_active || !_visible()) return;
        Clear(); var generation = _generation;
        var cancellation = (_renderCancellation = new()).Token;
        try
        {
            if (_kind == "cards")
            {
                if (!_settings.Enabled)
                {
                    Children.Add(new TextBlock { Text = "列表模式示例", FontSize = 14 });
                    for (var i = 0; i < 3; i++)
                    {
                        var card = NewCard(i); _cards.Add(card);
                        var label = new TextBlock { FontSize = 11, Opacity = .65 };
                        var host = new StackPanel { Children = { label, card } }; _slots.Add((host, label)); Children.Add(host);
                    }
                    Children.Add(new TextBlock { Text = "打开右侧卡片模式开关，预览固定网格。", FontSize = 12, Opacity = 0.65 });
                    _feed.Start();
                    return;
                }
                var grid = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
                var columns = Math.Min(_settings.Columns, Math.Max(1, (int)((Math.Max(200, ActualWidth) + 6) / 206)));
                Children.Add(new TextBlock { Text = $"固定槽位示例 · 当前 {columns} 列（期望 {_settings.Columns}）· {_settings.Height} px", FontSize = 14, TextWrapping = TextWrapping.Wrap });
                for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                for (var i = 0; i < 2; i++) grid.RowDefinitions.Add(new() { Height = new(_settings.Height) });
                for (var i = 0; i < columns * 2; i++)
                {
                    var card = NewCard(i); _cards.Add(card);
                    var label = new TextBlock { Text = $"×{(i == 0 ? 3 : 1)}   {DateTime.Now:HH:mm:ss}", FontSize = 12 };
                    var stack = new StackPanel { Spacing = 5, Children = { label, card } }; _slots.Add((stack, label));
                    var frame = new Border { Background = MessageCard.Brush("#252B36"), CornerRadius = new(7), Padding = new(6), Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
                    Grid.SetRow(frame, i / columns); Grid.SetColumn(frame, i % columns); grid.Children.Add(frame);
                }
                Children.Add(grid); Children.Add(new TextBlock { Text = "重复只更新次数 · 悬停保护 · " + (_settings.AutoHide ? $"累计 {_settings.RepeatThreshold} 次后隐藏 {_settings.HideSeconds} 秒" : "自动隐藏已关闭") + "\n这组本地示例不会写入实际卡片", FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.65 });
                _feed.Start();
                return;
            }
            Children.Add(_provider);
            var reference = _provider.SelectedIndex == 1 ? new MediaReference(MediaKind.Steam, "730") : new(MediaKind.Bilibili, "BV1xx411c7mD");
            var details = reference.Kind == MediaKind.Steam ? new MediaDetails(reference, "本地游戏示例", Creator: "示例开发商", Summary: "¥ 68.00（示例价格）")
                : new(reference, "本地视频标题示例：带封面、作者和播放量", Creator: "示例作者", Summary: "播放 12,345");
            Children.Add(new TextBlock { Text = "概览媒体示例", FontSize = 14 });
            if (_media.DashboardEnabled)
            {
                _tile = new(reference, _vm, _clock, () => _active && _visible(), _media, _style, details);
                if (_media.Mode == MediaPreviewMode.OnClick) Children.Add(new Expander { Header = $"{(reference.Kind == MediaKind.Bilibili ? "视频" : "Steam")} · {reference.Id}", Content = _tile, HorizontalAlignment = HorizontalAlignment.Stretch });
                else Children.Add(_tile);
            }
            else Children.Add(new TextBlock { Text = "概览媒体已关闭", Opacity = 0.65 });
            Children.Add(new TextBlock { Text = "悬浮消息示例 · 速度沿用普通弹幕设置", FontSize = 14 });
            if (_media.OverlayEnabled)
            {
                var region = OverlayRegions.ForRendering(_region);
                using var body = OverlayMessageRenderer.Render(Sample(0), region, _style, _vm.Images, maxHeight: 80);
                using var media = new OverlayMediaAttachment(_vm, reference, 80, region, _media, details, false);
                using var bitmap = media.Compose(body, region);
                var source = await UiPixelSource.CreateAsync(BiliPixelFrame.FromDrawingBitmap(bitmap), bitmap.Width, false, 256, cancellation);
                if (_disposed || !_active || generation != _generation) { source.Dispose(); return; }
                _pixelSources.Add(source);
                var width = Math.Max(200, ActualWidth);
                var canvas = new Canvas { Height = bitmap.Height + 16, Clip = new RectangleGeometry { Rect = new(0, 0, width, bitmap.Height + 16) } };
                var image = new Image { Source = source.Source, Width = bitmap.Width, Height = bitmap.Height, Stretch = Stretch.Fill, Opacity = region.Opacity };
                Canvas.SetTop(image, 8); canvas.Children.Add(image); Children.Add(canvas);
                ElementCompositionPreview.SetIsTranslationEnabled(image, true); _mediaVisual = ElementCompositionPreview.GetElementVisual(image);
                using var animation = _mediaVisual.Compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(0, (float)width); animation.InsertKeyFrame(1, -bitmap.Width, _mediaVisual.Compositor.CreateLinearEasingFunction());
                animation.Duration = TimeSpan.FromSeconds((width + bitmap.Width) / Math.Max(30, region.ScrollSpeed)); animation.IterationBehavior = AnimationIterationBehavior.Forever;
                _mediaVisual.StartAnimation("Translation.X", animation);
            }
            else Children.Add(new TextBlock { Text = "悬浮媒体已关闭", Opacity = 0.65 });
            Children.Add(new TextBlock { Text = "预览只使用本地资料与占位封面，不访问 B站或 Steam。", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_disposed && generation == _generation) Children.Add(new TextBlock { Text = "本地预览暂不可用", FontSize = 12 }); }
    }
    private MessageCard NewCard(int index)
    {
        var card = new MessageCard(new(Sample(index)), _vm, _clock, () => _active && _visible(), false, _style);
        card.Activate(XamlRoot?.RasterizationScale ?? 1); return card;
    }
    private static LiveEvent Sample(int index) => new(LiveEventType.Danmaku, "示例观众", index == 0 ? "今天的直播很好看 😊" : $"第 {index + 1} 个固定位置", "#FFFFFF", index + 1,
        Appearance: new LiveMessageAppearance { MedalName = "示例牌", MedalLevel = 21, MedalColor = "#4388BC", GuardLevel = 3 });
    public void Dispose() { if (_disposed) return; _disposed = true; _renderTimer.Stop(); Clear(); _clock.Dispose(); }
}
