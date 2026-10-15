using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;

namespace BilibiliLiveAssistant.App;

// UI-thread owned. The input SoftwareBitmap lives through the complete upload,
// including cancellation; only its completed result may be bound to a control.
internal sealed class UiPixelSource : IDisposable
{
    // Includes settings previews as well as shared URL-backed display sources.
    private static readonly SemaphoreSlim UploadSlots = new(2, 2);
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    public SoftwareBitmapSource Source { get; }
    public long Bytes { get; }
    public bool CanAnimate { get; }
    public int DecodeLimit { get; }
    public int PixelWidth { get; }
    private bool _disposed;
    private UiPixelSource(SoftwareBitmapSource source, BiliPixelFrame pixels, bool animated, int limit)
    {
        Source = source; Bytes = pixels.Bytes; CanAnimate = animated; DecodeLimit = limit; PixelWidth = pixels.Width;
        Interlocked.Increment(ref ImageResourceCounters.Sources);
        Interlocked.Increment(ref ImageResourceCounters.Created);
        Interlocked.Add(ref ImageResourceCounters.SourceBytes, Bytes);
    }
    internal static async Task<UiPixelSource> CreateAsync(BiliPixelFrame pixels, int width, bool animated, int limit, CancellationToken cancellation)
    {
        var entered = false;
        Interlocked.Increment(ref ImageResourceCounters.PendingUploads);
        try
        {
            await UploadSlots.WaitAsync(cancellation); entered = true;
        }
        finally { Interlocked.Decrement(ref ImageResourceCounters.PendingUploads); }
        Interlocked.Increment(ref ImageResourceCounters.ActiveUploads);
        try
        {
            BiliPixelFrame scaled;
            using (ImageResourceCounters.Measure(1))
                scaled = await Task.Run(() => { cancellation.ThrowIfCancellationRequested(); return pixels.AtWidth(width); }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            using var bitmap = scaled.CreateSoftwareBitmap();
            var result = new UiPixelSource(new SoftwareBitmapSource(), scaled, animated, limit);
            try
            {
                using var timing = ImageResourceCounters.Measure(2);
                // Do not cancel just the wait: the input must stay alive until the
                // native operation completes, even when the consumer disappeared.
                await result.Source.SetBitmapAsync(bitmap);
                if (cancellation.IsCancellationRequested) Interlocked.Increment(ref ImageResourceCounters.StaleUploads);
                cancellation.ThrowIfCancellationRequested();
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally
        {
            Interlocked.Decrement(ref ImageResourceCounters.ActiveUploads);
            if (entered) UploadSlots.Release();
        }
    }
    public void Dispose()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("图片显示源必须由所属 UI 线程释放");
        if (_disposed) return;
        using var timing = ImageResourceCounters.Measure(3);
        _disposed = true; Source.Dispose();
        Interlocked.Decrement(ref ImageResourceCounters.Sources);
        Interlocked.Increment(ref ImageResourceCounters.Released);
        Interlocked.Add(ref ImageResourceCounters.SourceBytes, -Bytes);
    }
}
