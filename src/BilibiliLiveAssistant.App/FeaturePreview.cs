using System.Drawing.Imaging;
using BilibiliLiveAssistant.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace BilibiliLiveAssistant.App;

/// <summary>Offline visual examples; drafts never query media providers.</summary>
internal sealed class FeaturePreview : StackPanel, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Func<bool> _visible;
    private readonly MessageAnimationClock _clock;
    private readonly List<MessageCard> _cards = new();
    private readonly DispatcherQueueTimer _renderTimer;
    private MediaPreviewTile? _tile;
    private bool _active, _disposed;
    private int _generation;
    private string _kind = "media";
    private MediaPreviewSettings _media = new();
    private DanmakuCardSettings _settings = new();
    private DashboardSettings _style = new();
    public FeaturePreview(LiveAssistantViewModel vm, Func<bool> visible)
    {
        _vm = vm; _visible = visible; _clock = new(() => _active && _visible()); Spacing = 12;
        _renderTimer = DispatcherQueue.GetForCurrentThread().CreateTimer(); _renderTimer.Interval = TimeSpan.FromMilliseconds(100); _renderTimer.IsRepeating = false;
        _renderTimer.Tick += (_, _) => Render();
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) Schedule(); };
    }
    public void Update(string kind, MediaPreviewSettings media, DanmakuCardSettings settings, DashboardSettings style)
    { _kind = kind; _media = media; _settings = settings; _style = style; Schedule(); }
    private void Schedule() { if (!_disposed && _active && _visible()) { _renderTimer.Stop(); _renderTimer.Start(); } }
    public void SetActive(bool active) { if (_disposed || _active == active) return; _active = active; if (active) Schedule(); else { _renderTimer.Stop(); Clear(); } }
    private void Clear()
    { _generation++; _tile?.Dispose(); _tile = null; foreach (var card in _cards) card.Dispose(); _cards.Clear(); Children.Clear(); _clock.Suspend(); }
    private async void Render()
    {
        if (_disposed || !_active || !_visible()) return;
        Clear(); var generation = _generation;
        try
        {
            if (_kind == "cards")
            {
                Children.Add(new TextBlock { Text = $"固定槽位示例 · {_settings.Columns} 列 · {_settings.Height} px", FontSize = 14 });
                var grid = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
                var columns = Math.Min(_settings.Columns, Math.Max(1, (int)(Math.Max(200, ActualWidth) / 200)));
                for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                for (var i = 0; i < 2; i++) grid.RowDefinitions.Add(new() { Height = new(_settings.Height) });
                for (var i = 0; i < columns * 2; i++)
                {
                    var message = Sample(i);
                    var card = new MessageCard(new(message), _vm, _clock, () => _active && _visible(), false, _style); _cards.Add(card);
                    var stack = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = $"×{(i == 0 ? 3 : 1)}   12:34:56   ◉", FontSize = 12 }, card } };
                    var frame = new Border { Background = MessageCard.Brush("#252B36"), CornerRadius = new(7), Padding = new(6), Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
                    Grid.SetRow(frame, i / columns); Grid.SetColumn(frame, i % columns); grid.Children.Add(frame);
                }
                Children.Add(grid); Children.Add(new TextBlock { Text = "重复只更新次数 · 悬停保护 · 这组本地示例不会写入实际卡片", FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.65 });
                return;
            }
            var reference = new MediaReference(MediaKind.Bilibili, "BV1fkPazmENy");
            var details = new MediaDetails(reference, "本地视频标题示例：带封面、作者和播放量", Creator: "示例作者", Summary: "播放 12,345");
            Children.Add(new TextBlock { Text = "概览媒体示例", FontSize = 14 });
            if (_media.DashboardEnabled)
            {
                _tile = new(reference, _vm, _clock, () => _active && _visible(), _media, _style, details);
                if (_media.Mode == MediaPreviewMode.OnClick) Children.Add(new Expander { Header = $"视频 · {reference.Id}", Content = _tile, HorizontalAlignment = HorizontalAlignment.Stretch });
                else Children.Add(_tile);
            }
            else Children.Add(new TextBlock { Text = "概览媒体已关闭", Opacity = 0.65 });
            Children.Add(new TextBlock { Text = "悬浮消息示例 · 速度沿用普通弹幕设置", FontSize = 14 });
            if (_media.OverlayEnabled)
            {
                var region = OverlayRegions.Get(_vm.Settings.Display, OverlayRegionKind.Danmaku);
                using var body = OverlayMessageRenderer.Render(Sample(0), region, _style, _vm.Images, maxHeight: 80);
                using var media = new OverlayMediaAttachment(_vm, reference, 80, region, _media, details, false);
                using var bitmap = media.Compose(body, region);
                using var memory = new MemoryStream(); bitmap.Save(memory, ImageFormat.Png);
                using var stream = new InMemoryRandomAccessStream(); using var writer = new DataWriter(stream.GetOutputStreamAt(0));
                writer.WriteBytes(memory.ToArray()); await writer.StoreAsync(); await writer.FlushAsync(); writer.DetachStream(); stream.Seek(0);
                var source = new BitmapImage(); await source.SetSourceAsync(stream);
                if (_disposed || !_active || generation != _generation) return;
                Children.Add(new Image { Source = source, Width = Math.Min(bitmap.Width, Math.Max(200, ActualWidth)), MaxWidth = bitmap.Width,
                    Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Opacity = region.Opacity });
            }
            else Children.Add(new TextBlock { Text = "悬浮媒体已关闭", Opacity = 0.65 });
            Children.Add(new TextBlock { Text = "预览只使用本地资料与占位封面，不访问 B站或 Steam。", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_disposed && generation == _generation) Children.Add(new TextBlock { Text = "本地预览暂不可用", FontSize = 12 }); }
    }
    private static LiveEvent Sample(int index) => new(LiveEventType.Danmaku, "示例观众", index == 0 ? "今天的直播很好看 😊" : $"第 {index + 1} 个固定位置", "#FFFFFF", index + 1,
        Appearance: new LiveMessageAppearance { MedalName = "示例牌", MedalLevel = 21, MedalColor = "#4388BC", GuardLevel = 3 });
    public void Dispose() { if (_disposed) return; _disposed = true; _renderTimer.Stop(); Clear(); _clock.Dispose(); }
}
