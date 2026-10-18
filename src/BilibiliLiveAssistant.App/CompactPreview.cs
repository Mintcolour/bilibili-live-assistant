using System.Drawing;
using BilibiliLiveAssistant.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PreviewImage = Microsoft.UI.Xaml.Controls.Image;

namespace BilibiliLiveAssistant.App;

internal sealed class CompactPreview : StackPanel, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Microsoft.UI.Xaml.Controls.Image _image = new() { Stretch = Stretch.Fill };
    private readonly Grid _surface = new();
    private readonly Canvas _listCanvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly Canvas _rankCanvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly Storyboard _rankAnimation = new() { RepeatBehavior = RepeatBehavior.Forever };
    private readonly List<UiPixelSource> _rowSources = new(4);
    private readonly List<(PreviewImage Image, CompositeTransform Transform, double TargetY, double FromY, bool Leaving, bool Entering)> _previewRows = new(4);
    private (UiPixelSource Source, int Width, int Height)[] _samples = [];
    private int _sampleStep, _listTop, _listHeight, _listWidth;
    private readonly Storyboard _animation = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 12 };
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _feed;
    private CancellationTokenSource? _cancellation;
    private UiPixelSource? _source;
    private AppSettings _settings;
    private bool _active, _disposed;
    private int _generation;
    private int _scAmount = 100;
    public CompactPreview(LiveAssistantViewModel vm)
    {
        _vm = vm; _settings = vm.Settings; Spacing = 10;
        var scTier = new ComboBox { Header = "示例 SC 档位", ItemsSource = new[] { 30, 50, 100, 500, 1000, 2000 }, SelectedIndex = 2 };
        Children.Add(scTier);
        _surface.Children.Add(_image); _surface.Children.Add(_listCanvas); _surface.Children.Add(_rankCanvas);
        Children.Add(new Viewbox { Child = _surface, Stretch = Stretch.Uniform, MaxHeight = 640, HorizontalAlignment = HorizontalAlignment.Left });
        var replay = UiFactory.Button("播放出入示例"); replay.Click += (_, _) => PlaySample();
        Children.Add(replay); Children.Add(_summary);
        _animation.Completed += (_, _) => FinishSample();
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer(); _timer.IsRepeating = false; _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => Render();
        _feed = DispatcherQueue.GetForCurrentThread().CreateTimer(); _feed.IsRepeating = false; _feed.Interval = TimeSpan.FromSeconds(2.4);
        _feed.Tick += (_, _) =>
        {
            if (!_active || _disposed || _previewRows.Count == 0) return;
            try
            {
                // Reuse the bounded bitmap pool; simulated arrivals do not decode new images.
                _sampleStep = (_sampleStep + 1) % _samples.Length; PresentRows(); _feed.Start();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { RuntimeErrorLog.Record("compact-preview-feed", ex); }
        };
        scTier.SelectionChanged += (_, _) => { if (scTier.SelectedItem is int amount) { _scAmount = amount; if (_active) { _timer.Stop(); _timer.Start(); } } };
    }
    public void Update(AppSettings settings)
    {
        // Editing OBS-only CSS must not repeatedly render or upload native preview bitmaps.
        var native = settings.Compact with { ObsTheme = _settings.Compact.ObsTheme };
        var unchanged = native == _settings.Compact && settings.Dashboard == _settings.Dashboard && settings.Display == _settings.Display
            && settings.PriceDurations.UnknownGuardSeconds == _settings.PriceDurations.UnknownGuardSeconds
            && settings.PriceDurations.SuperChat.SequenceEqual(_settings.PriceDurations.SuperChat) && settings.PriceDurations.Gift.SequenceEqual(_settings.PriceDurations.Gift);
        _settings = settings; if (_active && !unchanged) { _timer.Stop(); _timer.Start(); }
    }
    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return; _active = active;
        if (active) _timer.Start(); else { _timer.Stop(); Clear(); }
    }
    private void Clear()
    {
        _feed.Stop();
        _samples = []; _sampleStep = 0;
        _generation++; _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null;
        _animation.Stop(); _animation.Children.Clear(); _previewRows.Clear();
        _rankAnimation.Stop(); _rankAnimation.Children.Clear();
        foreach (var child in _rankCanvas.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<PreviewImage>())) child.Source = null;
        _rankCanvas.Children.Clear();
        foreach (var child in _listCanvas.Children.OfType<PreviewImage>()) child.Source = null;
        _listCanvas.Children.Clear(); foreach (var source in _rowSources) source.Dispose(); _rowSources.Clear();
        _image.Source = null; _source?.Dispose(); _source = null;
    }
    private void FinishSample()
    {
        _animation.Stop(); _animation.Children.Clear();
        foreach (var row in _previewRows)
        {
            row.Transform.TranslateY = row.TargetY; row.Image.Opacity = 1;
            row.Image.Visibility = row.Leaving ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    private void PlaySample()
    {
        if (!_active || _disposed) return;
        FinishSample();
        if (!_settings.Compact.ListAnimationEnabled) return;
        var duration = new Duration(TimeSpan.FromMilliseconds(_settings.Compact.ListAnimationDurationMs));
        void Add(DependencyObject target, string property, double from, double to)
        {
            var animation = new DoubleAnimation { From = from, To = to, Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property); _animation.Children.Add(animation);
        }
        foreach (var row in _previewRows)
        {
            row.Image.Visibility = Visibility.Visible;
            Add(row.Transform, "TranslateY", row.FromY, row.TargetY);
            if (row.Entering || row.Leaving) Add(row.Image, "Opacity", row.Entering ? 0 : 1, row.Leaving ? 0 : 1);
        }
        if (_animation.Children.Count > 0)
        {
            try { _animation.Begin(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { FinishSample(); RuntimeErrorLog.Record("compact-preview-animation", ex); _summary.Text = "动画预览暂不可用，已保留静态示例。"; }
        }
    }
    private async void Render()
    {
        if (!_active || _disposed) return;
        Clear(); var generation = _generation; var token = (_cancellation = new()).Token;
        UiPixelSource? backgroundSource = null;
        UiPixelSource? rankSource = null;
        CompactStatusStrip? status = null;
        var localRows = new List<(UiPixelSource Source, int Width, int Height)>(4);
        try
        {
            var settings = _settings; var c = settings.Compact; var width = Math.Min(600, c.ObsWidth); var height = Math.Min(800, c.ObsHeight);
            using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var listTop = 0; var listHeight = 0; var statusTop = 8;
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(System.Drawing.Color.Transparent);
                if (c.BackgroundOpacity > 0) { using var brush = new SolidBrush(OverlayTextRenderer.Color(c.BackgroundColor, c.BackgroundOpacity)); g.FillRectangle(brush, 0, 0, width, height); }
                var top = 8;
                if (c.ShowStatus)
                {
                    var region = ObsOverlayProjection.Region(settings, new(LiveEventType.Rank, "", "", "#B998FF"));
                    status = OverlayBulletinRenderer.CreateCompact(CompactExamples.Bulletin(), region, settings.Dashboard, _vm.Images, width - 16);
                    statusTop = top;
                    var saved = g.Save(); g.SetClip(new Rectangle(8, top, status.RankLeft, status.Height));
                    g.DrawImageUnscaled(status.Prefix, 8, top + (status.Height - status.Prefix.Height) / 2); g.Restore(saved);
                    top += status.Height + 4;
                }
                var messages = SampleMessages();
                void Draw(LiveEvent value, int maxHeight, bool card = true, bool inline = false)
                {
                    if (top + 40 >= height) return;
                    var region = ObsOverlayProjection.Region(settings, value);
                    if (card) maxHeight = Math.Max(maxHeight, OverlayMessageRenderer.MinimumCardHeight(region, settings.Dashboard,
                        value.Type is LiveEventType.Gift or LiveEventType.Guard ? 2 : 1));
                    using var row = OverlayMessageRenderer.Render(value, region, settings.Dashboard, _vm.Images, width - 16, Math.Min(maxHeight, height - top), card, background: false, inlineHeader: inline);
                    g.DrawImageUnscaled(row, 8, top); top += row.Height + 6;
                }
                if (c.ShowSuperChat) Draw(messages[0], c.FontSize * (c.SuperChatLines + 2) + 24);
                if (c.ShowGift) Draw(messages[1], c.FontSize * 3 + 24);
                var footer = (c.ShowEntry ? 54 : 0) + (c.ShowFollow ? 54 : 0);
                if (c.ShowList) { listTop = top + 10; listHeight = Math.Max(0, height - footer - listTop - 8); }
                if (c.ShowEntry) { top = Math.Max(top, height - footer); Draw(messages[3], 54, false); }
                if (c.ShowFollow) Draw(messages[4], 54, false);
            }
            backgroundSource = await UiPixelSource.CreateAsync(BiliPixelFrame.FromDrawingBitmap(bitmap), bitmap.Width, false, 256, token);
            if (status?.Members is { } rankImage)
                rankSource = await UiPixelSource.CreateAsync(BiliPixelFrame.FromDrawingBitmap(rankImage), rankImage.Width, false, 256, token);
            if (c.ShowList && listHeight >= 40)
            {
                var samples = SampleMessages();
                foreach (var value in new[] { samples[2] with { Content = "上一条弹幕从顶部离开。" }, samples[1],
                    samples[0] with { Content = "列表里的 SC 也会平滑上移。" }, samples[2] with { Content = "新弹幕从底部滑入，欢迎来到直播间！" } })
                {
                    token.ThrowIfCancellationRequested();
                    using var row = OverlayMessageRenderer.Render(value, ObsOverlayProjection.Region(settings, value), settings.Dashboard,
                        _vm.Images, width - 16, Math.Min(100, listHeight), card: true, background: false, inlineHeader: true);
                    var source = await UiPixelSource.CreateAsync(BiliPixelFrame.FromDrawingBitmap(row), row.Width, false, 256, token);
                    localRows.Add((source, row.Width, row.Height));
                }
            }
            if (_disposed || !_active || generation != _generation) return;
            _source = backgroundSource; backgroundSource = null; _image.Source = _source.Source;
            _surface.Width = width; _surface.Height = height; _surface.Opacity = c.Opacity;
            if (rankSource is not null && status?.Members is { } members)
            {
                var source = rankSource; _rowSources.Add(source); rankSource = null;
                _rankCanvas.Width = status.RankWidth; _rankCanvas.Height = status.Height;
                _rankCanvas.Margin = new Thickness(8 + status.RankLeft, statusTop, 0, 0);
                _rankCanvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, status.RankWidth, status.Height) };
                var transform = new CompositeTransform();
                var track = new Canvas { Width = status.Period + members.Width, Height = status.Height, RenderTransform = transform };
                foreach (var x in new[] { 0d, status.Period })
                {
                    var copy = new PreviewImage { Source = source.Source, Width = members.Width, Height = members.Height, Stretch = Stretch.None };
                    Canvas.SetLeft(copy, x); Canvas.SetTop(copy, (status.Height - members.Height) / 2d); track.Children.Add(copy);
                }
                _rankCanvas.Children.Add(track);
                var scroll = new DoubleAnimation { From = 0, To = -status.Period, Duration = new Duration(TimeSpan.FromSeconds(status.Period / CompactStatusStrip.Speed)) };
                Storyboard.SetTarget(scroll, transform); Storyboard.SetTargetProperty(scroll, "TranslateX"); _rankAnimation.Children.Add(scroll);
                _rankAnimation.Begin();
            }
            _samples = localRows.ToArray(); _listTop = listTop; _listHeight = listHeight; _listWidth = width - 16;
            _rowSources.AddRange(localRows.Select(row => row.Source)); localRows.Clear();
            PresentRows();
            if (_previewRows.Count > 0) _feed.Start();
            _summary.Text = $"混合列表持续模拟消息到达 · OBS CSS 请使用“打开真实网页预览”\nOBS {c.ObsWidth} × {c.ObsHeight} · 列表动画{(c.ListAnimationEnabled ? $" {c.ListAnimationDurationMs} 毫秒" : "已关闭")}\n示例 SC ¥{_scAmount}：{_settings.PriceDurations.Seconds(SampleMessages()[0]):0.#} 秒；礼物 ¥200：{_settings.PriceDurations.Seconds(CompactExamples.Messages()[1]):0.#} 秒。\n示例不进入真实消息或 OBS；保存后应用。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (generation == _generation) { Clear(); _summary.Text = "本地预览暂不可用。"; } }
        finally { status?.Dispose(); rankSource?.Dispose(); backgroundSource?.Dispose(); foreach (var row in localRows) row.Source.Dispose(); }
    }
    private void PresentRows()
    {
        FinishSample(); _previewRows.Clear();
        foreach (var image in _listCanvas.Children.OfType<PreviewImage>()) image.Source = null;
        _listCanvas.Children.Clear();
        var rows = Enumerable.Range(0, _samples.Length).Select(i => _samples[(i + _sampleStep) % _samples.Length]).ToArray();
        var visibleHeight = Math.Min(_listHeight, rows.Skip(1).Sum(row => row.Height + 4));
        _listCanvas.Width = _listWidth; _listCanvas.Height = visibleHeight;
        _listCanvas.Margin = new Thickness(8, _listTop + _listHeight - visibleHeight, 0, 0);
        _listCanvas.Clip = new RectangleGeometry { Rect = new(0, 0, _listWidth, visibleHeight) };
        var y = (double)visibleHeight;
        for (var i = rows.Length - 1; i >= 0; i--)
        {
            var row = rows[i]; y -= row.Height;
            var transform = new CompositeTransform { TranslateY = y };
            var image = new PreviewImage { Source = row.Source.Source, Width = row.Width, Height = row.Height, Stretch = Stretch.None, RenderTransform = transform };
            _listCanvas.Children.Add(image);
            _previewRows.Add((image, transform, y, i == 0 ? 0 : i == rows.Length - 1 ? y + 14 : y + rows[^1].Height + 4,
                i == 0, i == rows.Length - 1)); y -= 4;
        }
        PlaySample();
    }
    private LiveEvent[] SampleMessages()
    {
        var messages = CompactExamples.Messages();
        messages[0] = messages[0] with { HistoryFields = messages[0].HistoryFields! with { AmountMilliYuan = _scAmount * 1000L } };
        return messages;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); Clear(); }
}
