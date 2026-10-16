using System.Diagnostics;

namespace BilibiliLiveAssistant.App;

// Numeric-only gauges survive sampling being disabled. Timings allocate nothing.
internal static class ImageResourceCounters
{
    internal static long Views, ActiveViews, Sources, SourceBytes, BackingBitmapBytes, Created, Released, PendingUploads, ActiveUploads, StaleUploads;
    internal static long AvatarBindings, MissingAvatars;
    internal static long BindingAttempts, BindingChanges, RebindAttempts, RebindChanges;
    private static long _decodeInputs, _decodeInputPeak, _decodes, _decodesEnded;
    private static long _bodyStaging, _bodyStagingPeak;
    private static readonly long[] BindingReady = new long[2002], WaitingBindingReady = new long[2002];
    internal static void BoundReady(long started, bool waited)
    {
        if (started == 0) return;
        var bin = (int)Math.Clamp(Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds), 0, 2001);
        Interlocked.Increment(ref BindingReady[bin]);
        if (waited) Interlocked.Increment(ref WaitingBindingReady[bin]);
    }
    private static object ReadySummary(long[] histogram)
    {
        long total = 0, cumulative = 0;
        for (var i = 0; i < histogram.Length; i++) total += Interlocked.Read(ref histogram[i]);
        int? p50 = null, p95 = null;
        for (var i = 0; i < 2001 && total > 0; i++)
        {
            cumulative += Interlocked.Read(ref histogram[i]);
            if (p50 is null && cumulative >= Math.Ceiling(total * 0.5)) p50 = i;
            if (cumulative >= Math.Ceiling(total * 0.95)) { p95 = i; break; }
        }
        return new { count = total, p50UpperMs = p50, p95UpperMs = p95, over2000ms = Interlocked.Read(ref histogram[2001]) };
    }
    internal static void AddBodyStaging(int bytes)
    {
        var current = Interlocked.Add(ref _bodyStaging, bytes);
        long previous;
        do { previous = Interlocked.Read(ref _bodyStagingPeak); if (current <= previous) break; }
        while (Interlocked.CompareExchange(ref _bodyStagingPeak, current, previous) != previous);
    }
    private static readonly long[] SourceSizes = new long[4], DecodeSizes = new long[3];
    internal static DecodeInput BeginDecode(long bytes)
    {
        Interlocked.Increment(ref _decodes);
        var current = Interlocked.Add(ref _decodeInputs, bytes);
        long previous;
        do { previous = Interlocked.Read(ref _decodeInputPeak); if (current <= previous) break; }
        while (Interlocked.CompareExchange(ref _decodeInputPeak, current, previous) != previous);
        return new(bytes);
    }
    internal readonly struct DecodeInput(long bytes) : IDisposable
    {
        public void Dispose() { Interlocked.Add(ref _decodeInputs, -bytes); Interlocked.Increment(ref _decodesEnded); }
    }
    internal static void DecodedSize(int width, int height, int limit)
    {
        var side = Math.Max(width, height);
        Interlocked.Increment(ref SourceSizes[side <= 128 ? 0 : side <= 512 ? 1 : side <= 2048 ? 2 : 3]);
        Interlocked.Increment(ref DecodeSizes[limit <= 64 ? 0 : limit <= 128 ? 1 : 2]);
    }
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
        bindingAttempts = Interlocked.Read(ref BindingAttempts), bindingChanges = Interlocked.Read(ref BindingChanges),
        rebindAttempts = Interlocked.Read(ref RebindAttempts), rebindChanges = Interlocked.Read(ref RebindChanges),
        decodeInputBytes = Interlocked.Read(ref _decodeInputs), decodeInputPeakBytes = Interlocked.Read(ref _decodeInputPeak),
        bodyReadStagingBytes = Interlocked.Read(ref _bodyStaging), bodyReadStagingPeakBytes = Interlocked.Read(ref _bodyStagingPeak),
        bindingReadyLatency = new { all = ReadySummary(BindingReady), waited = ReadySummary(WaitingBindingReady),
            note = "Process-cumulative successful visible WinUI bindings; 1ms histogram. Null quantile means no data or over 2000ms. Not GPU presentation or overlay latency." },
        decodesStarted = Interlocked.Read(ref _decodes), decodesEnded = Interlocked.Read(ref _decodesEnded),
        sourceSizes = new { upTo128 = Interlocked.Read(ref SourceSizes[0]), upTo512 = Interlocked.Read(ref SourceSizes[1]), upTo2048 = Interlocked.Read(ref SourceSizes[2]), larger = Interlocked.Read(ref SourceSizes[3]) },
        decodeTiers = new { px64 = Interlocked.Read(ref DecodeSizes[0]), px128 = Interlocked.Read(ref DecodeSizes[1]), px256 = Interlocked.Read(ref DecodeSizes[2]) },
        decodeCount = Interlocked.Read(ref Counts[0]), decodeMs = Interlocked.Read(ref Ticks[0]) * 1000d / Stopwatch.Frequency,
        conversionCount = Interlocked.Read(ref Counts[1]), conversionMs = Interlocked.Read(ref Ticks[1]) * 1000d / Stopwatch.Frequency,
        uploadCount = Interlocked.Read(ref Counts[2]), uploadMs = Interlocked.Read(ref Ticks[2]) * 1000d / Stopwatch.Frequency,
        releaseCount = Interlocked.Read(ref Counts[3]), releaseMs = Interlocked.Read(ref Ticks[3]) * 1000d / Stopwatch.Frequency,
    };
}
