namespace BilibiliLiveAssistant.App;

internal static class ImageDecodeAdmission
{
    private static readonly SemaphoreSlim Large = new(1, 1);
    private static long _waiting, _active;
    internal static async ValueTask<Lease> EnterAsync(long sourcePixels, int bodyBytes, CancellationToken cancellation)
    {
        if (sourcePixels <= 1_048_576 && bodyBytes <= 512 * 1024) return new(false);
        Interlocked.Increment(ref _waiting);
        try { await Large.WaitAsync(cancellation).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _waiting); }
        Interlocked.Increment(ref _active);
        return new(true);
    }
    internal readonly struct Lease(bool held) : IDisposable
    { public void Dispose() { if (held) { Interlocked.Decrement(ref _active); Large.Release(); } } }
    internal static object Snapshot() => new { largeWaiting = Interlocked.Read(ref _waiting), largeActive = Interlocked.Read(ref _active) };
}
