using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

/// <summary>One admitted overlay consumer; global count is shared by danmaku and SC.</summary>
internal sealed class OverlayMediaAttachment : IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly MediaReference _reference;
    private readonly CancellationTokenSource _cancel = new();
    private readonly MediaPreviewSettings _settings;
    private MediaDetails? _details;
    private bool _disposed;
    private bool _started, _finished;
    public int BodyHeight { get; }
    public int Height { get; }
    public OverlayMediaAttachment(LiveAssistantViewModel vm, MediaReference reference, int bodyHeight, OverlayRegionSettings region)
    {
        _vm = vm; _reference = reference; _settings = vm.Settings.MediaPreview;
        BodyHeight = bodyHeight; Height = RequiredHeight(bodyHeight, _settings, region);
    }
    public static int RequiredHeight(int bodyHeight, MediaPreviewSettings settings, OverlayRegionSettings region)
        => bodyHeight + settings.CoverHeight + (int)Math.Ceiling(Math.Clamp(region.FontSize * 0.65, 14, 28) * 3 + Math.Clamp(region.FontSize * 0.5, 12, 16) * 2.8) + 24;
    public async Task LoadAsync(Action changed)
    {
        _started = true;
        try
        {
            _details = await _vm.Media.GetForViewAsync(_reference, _cancel.Token);
            if (_disposed) return; changed();
            var urls = new[] { _details.Cover, _vm.Settings.Dashboard.ShowAvatars ? _details.CreatorAvatar : "" }.Where(x => x.Length > 0).Distinct();
            await Task.WhenAll(urls.Select(LoadImage));
            async Task LoadImage(string url)
            {
                if (await _vm.Images.GetForViewAsync(url, false, _cancel.Token) is not null && !_disposed) changed();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_disposed) { _details = new(_reference, _reference.Id, Error: "资料暂不可用"); changed(); } }
        finally { _finished = true; if (_disposed) _cancel.Dispose(); }
    }
    public Bitmap Compose(Bitmap body, OverlayRegionSettings region)
    {
        var output = new Bitmap(Math.Max(body.Width, _settings.Width), Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(output); graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.DrawImageUnscaled(body, 0, 0);
        var top = BodyHeight + 4;
        using var shape = OverlayTextRenderer.RoundedRectangle(new RectangleF(0, top, _settings.Width, Height - top), 8);
        using var fill = new SolidBrush(Color.FromArgb(240, 31, 36, 47)); graphics.FillPath(fill, shape);
        var details = _details; var cover = details?.Cover ?? "";
        var bounds = new RectangleF(8, top + 8, _settings.Width - 16, _settings.CoverHeight);
        DrawImage(graphics, cover, bounds);
        using var titleFont = OverlayTextRenderer.CreateFont(region.FontFamily, Math.Clamp(region.FontSize * 0.65f, 14, 28), true);
        using var smallFont = OverlayTextRenderer.CreateFont(region.FontFamily, Math.Clamp(region.FontSize * 0.5f, 12, 16));
        var y = bounds.Bottom + 5;
        var title = details?.Title is { Length: > 0 } text ? text : _reference.Id;
        DrawLines(graphics, title, titleFont, new(8, y), _settings.Width - 16, 2);
        y += titleFont.GetHeight(graphics) * 2 + 2;
        var creator = details?.Creator ?? (details is null ? "加载中…" : "");
        var avatar = details?.CreatorAvatar ?? "";
        var x = 8f;
        if (_vm.Settings.Dashboard.ShowAvatars && avatar.Length > 0) { DrawImage(graphics, avatar, new(x, y, 18, 18)); x += 22; }
        DrawLines(graphics, creator, smallFont, new(x, y), _settings.Width - x - 8, 1);
        y += smallFont.GetHeight(graphics) + 3;
        DrawLines(graphics, details is null ? "正在获取媒体资料" : details.IsSuccess ? details.Summary : details.Error, smallFont, new(8, y), _settings.Width - 16, 1);
        return output;
    }
    private void DrawImage(Graphics graphics, string url, RectangleF bounds)
    {
        var bytes = _vm.Images.TryGetFrame(url);
        if (bytes is null) { using var brush = new SolidBrush(Color.FromArgb(255, 54, 64, 83)); graphics.FillRectangle(brush, bounds); return; }
        try
        {
            using var stream = new MemoryStream(bytes); using var bitmap = new Bitmap(stream);
            var ratio = Math.Min(bounds.Width / bitmap.Width, bounds.Height / bitmap.Height);
            var width = bitmap.Width * ratio; var height = bitmap.Height * ratio;
            graphics.DrawImage(bitmap, new RectangleF(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height));
        }
        catch (ArgumentException) { }
    }
    private static void DrawLines(Graphics graphics, string value, Font font, PointF location, float width, int lines)
    {
        // Bounded metadata strings are laid out only when the cached bitmap changes.
        var remaining = value.Replace('\r', ' ').Replace('\n', ' ');
        for (var line = 0; line < lines && remaining.Length > 0; line++)
        {
            var low = 0; var high = remaining.Length;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                var suffix = middle < remaining.Length && line == lines - 1 ? "…" : "";
                if (OverlayTextRenderer.Measure(graphics, remaining[..middle] + suffix, font).Width <= width) low = middle;
                else high = middle - 1;
            }
            var count = low;
            if (count > 0 && count < remaining.Length && char.IsHighSurrogate(remaining[count - 1])) count--;
            if (count == 0) break;
            var text = remaining[..count] + (count < remaining.Length && line == lines - 1 ? "…" : "");
            OverlayTextRenderer.Draw(graphics, text, font, new(location.X, location.Y + line * font.GetHeight(graphics)), Color.White, false);
            remaining = remaining[count..];
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _cancel.Cancel();
        _vm.ReleaseOverlayMedia();
        if (!_started || _finished) _cancel.Dispose();
    }
}
