using System.Diagnostics;

namespace BilibiliLiveAssistant.App;

// Numeric-only gauges survive sampling being disabled. Timings allocate nothing.
internal static class ImageResourceCounters
{
    internal static long Views, ActiveViews;
    private static readonly long[] Counts = new long[3], Ticks = new long[3];
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
        decodeCount = Interlocked.Read(ref Counts[0]), decodeMs = Interlocked.Read(ref Ticks[0]) * 1000d / Stopwatch.Frequency,
        conversionCount = Interlocked.Read(ref Counts[1]), conversionMs = Interlocked.Read(ref Ticks[1]) * 1000d / Stopwatch.Frequency,
        uploadCount = Interlocked.Read(ref Counts[2]), uploadMs = Interlocked.Read(ref Ticks[2]) * 1000d / Stopwatch.Frequency,
    };
}
