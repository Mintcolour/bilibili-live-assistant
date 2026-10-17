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
    private readonly List<UiPixelSource> _rowSources = new(4);
    private readonly List<(PreviewImage Image, CompositeTransform Transform, double TargetY, double FromY, bool Leaving, bool Entering)> _previewRows = new(4);
    private readonly Storyboard _animation = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 12 };
    private readonly DispatcherQueueTimer _timer;
    private CancellationTokenSource? _cancellation;
    private UiPixelSource? _source;
    private AppSettings _settings;
    private bool _active, _disposed;
    private int _generation;
    public CompactPreview(LiveAssistantViewModel vm)
    {
        _vm = vm; _settings = vm.Settings; Spacing = 10;
        _surface.Children.Add(_image); _surface.Children.Add(_listCanvas);
        Children.Add(new Viewbox { Child = _surface, Stretch = Stretch.Uniform, MaxHeight = 640, HorizontalAlignment = HorizontalAlignment.Left });
        var replay = UiFactory.Button("播放出入示例"); replay.Click += (_, _) => PlaySample();
        Children.Add(replay); Children.Add(_summary);
        _animation.Completed += (_, _) => FinishSample();
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer(); _timer.IsRepeating = false; _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => Render();
    }
    public void Update(AppSettings settings) { _settings = settings; if (_active) { _timer.Stop(); _timer.Start(); } }
    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return; _active = active;
        if (active) _timer.Start(); else { _timer.Stop(); Clear(); }
    }
    private void Clear()
    {
        _generation++; _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null;
        _animation.Stop(); _animation.Children.Clear(); _previewRows.Clear();
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
        var localRows = new List<(UiPixelSource Source, int Width, int Height)>(4);
        try
        {
            var settings = _settings; var c = settings.Compact; var width = Math.Min(600, c.ObsWidth); var height = Math.Min(800, c.ObsHeight);
            using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var listTop = 0; var listHeight = 0;
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(System.Drawing.Color.Transparent);
                if (c.BackgroundOpacity > 0) { using var brush = new SolidBrush(OverlayTextRenderer.Color(c.BackgroundColor, c.BackgroundOpacity)); g.FillRectangle(brush, 0, 0, width, height); }
                var top = 8;
                if (c.ShowStatus) { using var font = OverlayTextRenderer.CreateFont("Microsoft YaHei UI", 15); OverlayTextRenderer.Draw(g, "◉ 1,234     👁 2.4万     #1 示例舰长", font, new(8, top), System.Drawing.Color.LightSteelBlue, false); top += 32; }
                var messages = CompactExamples.Messages();
                void Draw(LiveEvent value, int maxHeight, bool card = true, bool inline = false)
                {
                    if (top + 40 >= height) return;
                    var region = ObsOverlayProjection.Region(settings, value);
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
            if (c.ShowList && listHeight >= 40)
            {
                var samples = CompactExamples.Messages();
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
            var visibleHeight = Math.Min(listHeight, localRows.Skip(1).Sum(row => row.Height + 4));
            _listCanvas.Width = width - 16; _listCanvas.Height = visibleHeight;
            _listCanvas.Margin = new Thickness(8, listTop + listHeight - visibleHeight, 0, 0);
            _listCanvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width - 16, visibleHeight) };
            var preparedRows = localRows.ToArray();
            _rowSources.AddRange(localRows.Select(row => row.Source)); localRows.Clear();
            var y = (double)visibleHeight;
            for (var i = preparedRows.Length - 1; i >= 0; i--)
            {
                var row = preparedRows[i]; y -= row.Height;
                var transform = new CompositeTransform { TranslateY = y };
                var image = new PreviewImage { Source = row.Source.Source, Width = row.Width, Height = row.Height, Stretch = Stretch.None, RenderTransform = transform };
                _listCanvas.Children.Add(image);
                _previewRows.Add((image, transform, y, i == 0 ? 0 : i == preparedRows.Length - 1 ? y + 14 : y + preparedRows[^1].Height + 4,
                    i == 0, i == preparedRows.Length - 1)); y -= 4;
            }
            PlaySample();
            _summary.Text = $"本地示例 · OBS {c.ObsWidth} × {c.ObsHeight} · 列表动画{(c.ListAnimationEnabled ? $" {c.ListAnimationDurationMs} 毫秒" : "已关闭")}\n示例 SC ¥100：{_settings.PriceDurations.Seconds(CompactExamples.Messages()[0]):0.#} 秒；礼物 ¥200：{_settings.PriceDurations.Seconds(CompactExamples.Messages()[1]):0.#} 秒。\n示例不进入真实消息或 OBS；保存后应用。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (generation == _generation) { Clear(); _summary.Text = "本地预览暂不可用。"; } }
        finally { backgroundSource?.Dispose(); foreach (var row in localRows) row.Source.Dispose(); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); Clear(); }
}
