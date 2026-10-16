using System.Diagnostics;

namespace BilibiliLiveAssistant.App;

// All timing updates and drains run on the UI thread. Disabled scopes allocate nothing.
internal static class PerformanceCounters
{
    internal static volatile bool Enabled;
    private static readonly long[] Counts = new long[10], Ticks = new long[10], MaxTicks = new long[10];
    internal static long CanvasBytes;
    internal static int Canvases;
    private static long _intervals, _lateFrames, _maxFrameTicks;
    private static long _blits, _sourcePixels, _blitPixels;
    private static long _imageInvalidations, _imageInvalidationsMerged;
    internal static void ImageInvalidation(bool merged)
    { if (!Enabled) return; _imageInvalidations++; if (merged) _imageInvalidationsMerged++; }
    internal static void Blit(long sourcePixels, long blitPixels)
    {
        if (!Enabled) return;
        _blits++; _sourcePixels += sourcePixels; _blitPixels += blitPixels;
    }
    internal static void FrameInterval(long ticks, int targetFps)
    {
        if (!Enabled || ticks <= 0) return;
        _intervals++; if (ticks > Stopwatch.Frequency * 1.5 / targetFps) _lateFrames++;
        _maxFrameTicks = Math.Max(_maxFrameTicks, ticks);
    }
    internal static Timing Measure(int kind) => new(kind, Enabled ? Stopwatch.GetTimestamp() : 0);
    internal readonly struct Timing(int kind, long started) : IDisposable
    {
        public void Dispose()
        {
            if (started == 0 || !Enabled) return;
            var elapsed = Stopwatch.GetTimestamp() - started;
            Counts[kind]++; Ticks[kind] += elapsed; MaxTicks[kind] = Math.Max(MaxTicks[kind], elapsed);
        }
    }
    internal static object Drain()
    {
        var names = new[] { "eventBatch", "frameCallbacks", "messageBitmap", "danmakuPaint", "giftPaint", "scPaint", "interactionPaint", "rankPaint", "overlayRaster", "overlayPresent" };
        var result = new object[names.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = new { name = names[i], count = Counts[i], totalMs = Ticks[i] * 1000d / Stopwatch.Frequency,
                maxMs = MaxTicks[i] * 1000d / Stopwatch.Frequency };
        Array.Clear(Counts); Array.Clear(Ticks); Array.Clear(MaxTicks);
        var cadence = new { intervals = _intervals, lateFrames = _lateFrames, maxGapMs = _maxFrameTicks * 1000d / Stopwatch.Frequency };
        _intervals = _lateFrames = _maxFrameTicks = 0;
        var blits = new { count = _blits, sourcePixels = _sourcePixels, submittedPixels = _blitPixels };
        var imageInvalidations = new { requested = _imageInvalidations, merged = _imageInvalidationsMerged };
        _imageInvalidations = _imageInvalidationsMerged = 0;
        _blits = _sourcePixels = _blitPixels = 0;
        return new { operations = result, frameCadence = cadence, blits, imageInvalidations };
    }
    internal static void Reset() => _ = Drain();
}
