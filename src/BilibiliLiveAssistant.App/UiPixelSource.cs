using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Imaging;

namespace BilibiliLiveAssistant.App;

// UI-thread owned. SoftwareBitmapSource keeps its input for later surface
// copies/recovery as well as the first upload, and closes that input itself.
internal sealed class UiPixelSource : IDisposable
{
    // Includes settings previews as well as shared URL-backed display sources.
    private static readonly SemaphoreSlim UploadSlots = new(2, 2);
    private static readonly SemaphoreSlim PressureSlot = new(1, 1);
    internal static bool SerializeForPressure { get; set; } // UI-thread only
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    public SoftwareBitmapSource Source { get; }
    public long BitmapBytes { get; }
    public long Bytes { get; }
    public bool CanAnimate { get; }
    public int DecodeLimit { get; }
    public int PixelWidth { get; }
    private bool _disposed;
    private UiPixelSource(SoftwareBitmapSource source, BiliPixelFrame pixels, bool animated, int limit)
    {
        Source = source; BitmapBytes = pixels.Bytes; Bytes = checked(BitmapBytes * 2);
        CanAnimate = animated; DecodeLimit = limit; PixelWidth = pixels.Width;
        Interlocked.Increment(ref ImageResourceCounters.Sources);
        Interlocked.Increment(ref ImageResourceCounters.Created);
        Interlocked.Add(ref ImageResourceCounters.SourceBytes, Bytes);
        Interlocked.Add(ref ImageResourceCounters.BackingBitmapBytes, BitmapBytes);
    }
    internal static async Task<UiPixelSource> CreateAsync(BiliPixelFrame pixels, int width, bool animated, int limit, CancellationToken cancellation)
    {
        var entered = false;
        var pressureEntered = false;
        Interlocked.Increment(ref ImageResourceCounters.PendingUploads);
        try
        {
            await UploadSlots.WaitAsync(cancellation); entered = true;
        }
        finally { Interlocked.Decrement(ref ImageResourceCounters.PendingUploads); }
        Interlocked.Increment(ref ImageResourceCounters.ActiveUploads);
        try
        {
            if (SerializeForPressure) { await PressureSlot.WaitAsync(cancellation); pressureEntered = true; }
            BiliPixelFrame scaled;
            using (ImageResourceCounters.Measure(1))
                scaled = await Task.Run(() => { cancellation.ThrowIfCancellationRequested(); return pixels.AtWidth(width); }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var result = new UiPixelSource(new SoftwareBitmapSource(), scaled, animated, limit);
            SoftwareBitmap? unownedBitmap = null;
            try
            {
                using var timing = ImageResourceCounters.Measure(2);
                unownedBitmap = scaled.CreateSoftwareBitmap();
                var upload = result.Source.SetBitmapAsync(unownedBitmap);
                // Transfer ownership to the native source. Completing this action
                // does not end the bitmap's lifetime: subsequent layout/rendering
                // may copy its pixels again. Source.Dispose() closes it, deferring
                // that close itself while a native copy is still in progress.
                unownedBitmap = null;
                // A cancelled consumer still waits for this native action before
                // disposing the obsolete source; never cancel just the wait.
                await upload;
                if (cancellation.IsCancellationRequested) Interlocked.Increment(ref ImageResourceCounters.StaleUploads);
                cancellation.ThrowIfCancellationRequested();
                return result;
            }
            catch { result.Dispose(); throw; }
            finally { unownedBitmap?.Dispose(); }
        }
        finally
        {
            Interlocked.Decrement(ref ImageResourceCounters.ActiveUploads);
            if (pressureEntered) PressureSlot.Release();
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
        Interlocked.Add(ref ImageResourceCounters.BackingBitmapBytes, -BitmapBytes);
    }
}
