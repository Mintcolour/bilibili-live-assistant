using System.Drawing;
using BilibiliLiveAssistant.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BilibiliLiveAssistant.App;

internal sealed class CompactPreview : StackPanel, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Microsoft.UI.Xaml.Controls.Image _image = new() { Stretch = Stretch.Uniform, MaxHeight = 640, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 12 };
    private readonly DispatcherQueueTimer _timer;
    private CancellationTokenSource? _cancellation;
    private UiPixelSource? _source;
    private AppSettings _settings;
    private bool _active, _disposed;
    private int _generation;
    public CompactPreview(LiveAssistantViewModel vm)
    {
        _vm = vm; _settings = vm.Settings; Spacing = 10; Children.Add(_image); Children.Add(_summary);
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
    { _generation++; _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null; _image.Source = null; _source?.Dispose(); _source = null; }
    private async void Render()
    {
        if (!_active || _disposed) return;
        Clear(); var generation = _generation; var token = (_cancellation = new()).Token;
        try
        {
            var c = _settings.Compact; var width = Math.Min(600, c.ObsWidth); var height = Math.Min(800, c.ObsHeight);
            using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
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
                    var region = ObsOverlayProjection.Region(_settings, value);
                    using var row = OverlayMessageRenderer.Render(value, region, _settings.Dashboard, _vm.Images, width - 16, Math.Min(maxHeight, height - top), card, background: false, inlineHeader: inline);
                    g.DrawImageUnscaled(row, 8, top); top += row.Height + 6;
                }
                if (c.ShowSuperChat) Draw(messages[0], c.FontSize * (c.SuperChatLines + 2) + 24);
                if (c.ShowGift) Draw(messages[1], c.FontSize * 3 + 24);
                var footer = (c.ShowEntry ? 54 : 0) + (c.ShowFollow ? 54 : 0);
                if (c.ShowList) { top = Math.Max(top + 10, height - footer - 180); Draw(messages[2], 100, inline: true); Draw(messages[0] with { Content = "混合列表保留 SC 全文，点击可查看。" }, 100, inline: true); }
                if (c.ShowEntry) { top = Math.Max(top, height - footer); Draw(messages[3], 54, false); }
                if (c.ShowFollow) Draw(messages[4], 54, false);
            }
            var source = await UiPixelSource.CreateAsync(BiliPixelFrame.FromDrawingBitmap(bitmap), bitmap.Width, false, 256, token);
            if (_disposed || !_active || generation != _generation) { source.Dispose(); return; }
            _source = source; _image.Source = source.Source; _image.Opacity = c.Opacity;
            _summary.Text = $"本地静态示例 · OBS {c.ObsWidth} × {c.ObsHeight}\n示例 SC ¥100：{_settings.PriceDurations.Seconds(CompactExamples.Messages()[0]):0.#} 秒；礼物 ¥200：{_settings.PriceDurations.Seconds(CompactExamples.Messages()[1]):0.#} 秒。\n示例不进入真实消息或 OBS；保存后应用。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (generation == _generation) _summary.Text = "本地预览暂不可用。"; }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); Clear(); }
}
