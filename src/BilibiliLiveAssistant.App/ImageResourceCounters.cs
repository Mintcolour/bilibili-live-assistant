using System.Diagnostics;

namespace BilibiliLiveAssistant.App;

// Numeric-only gauges survive sampling being disabled. Timings allocate nothing.
internal static class ImageResourceCounters
{
    internal static long Views, ActiveViews, Sources, SourceBytes, BackingBitmapBytes, Created, Released, PendingUploads, ActiveUploads, StaleUploads;
    internal static long AvatarBindings, MissingAvatars;
    private static readonly long[] Counts = new long[4], Ticks = new long[4];
    internal static Timing Measure(int kind) => new(kind, PerformanceCounters.Enabled ? Stopwatch.GetTimestamp() : 0);
    internal readonly struct Timing(int kind, long started) : IDisposable
    {
        public void Dispose()
        {
            if (started == 0) return;
            Interlocked.Increment(ref Counts[kind]);
            Interlocked.Add(ref Ticks[kind], Stopwatch.GetTimestamp() - started);
        }
    }
    internal static object Snapshot() => new
    {
        views = Interlocked.Read(ref Views), activeViews = Interlocked.Read(ref ActiveViews),
        nativeSources = Interlocked.Read(ref Sources), nativePixelBytesEstimate = Interlocked.Read(ref SourceBytes),
        backingBitmapBytes = Interlocked.Read(ref BackingBitmapBytes),
        displaySurfaceBytesEstimate = Interlocked.Read(ref BackingBitmapBytes),
        pixelAccounting = "backing-plus-surface-v2",
        created = Interlocked.Read(ref Created), released = Interlocked.Read(ref Released),
        pendingUploads = Interlocked.Read(ref PendingUploads), activeUploads = Interlocked.Read(ref ActiveUploads), staleUploads = Interlocked.Read(ref StaleUploads),
        avatarBindings = Interlocked.Read(ref AvatarBindings), missingAvatars = Interlocked.Read(ref MissingAvatars),
        decodeCount = Interlocked.Read(ref Counts[0]), decodeMs = Interlocked.Read(ref Ticks[0]) * 1000d / Stopwatch.Frequency,
        conversionCount = Interlocked.Read(ref Counts[1]), conversionMs = Interlocked.Read(ref Ticks[1]) * 1000d / Stopwatch.Frequency,
        uploadCount = Interlocked.Read(ref Counts[2]), uploadMs = Interlocked.Read(ref Ticks[2]) * 1000d / Stopwatch.Frequency,
        releaseCount = Interlocked.Read(ref Counts[3]), releaseMs = Interlocked.Read(ref Ticks[3]) * 1000d / Stopwatch.Frequency,
    };
}
