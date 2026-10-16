namespace BilibiliLiveAssistant.App;

// Share only active downloads/decodes, never retain a second original-image
// cache. Each consumer cancels its own wait. The last one closes the flight.
internal sealed class SharedImageBodies(Func<Uri, CancellationToken, Task<byte[]>> read, CancellationToken shutdown) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Flight> _pending = new();
    private readonly SemaphoreSlim _downloads = new(2, 2);
    private TaskCompletionSource _capacity = NewSignal();
    private bool _disposed;
    private long _bytes, _peak, _shared, _started, _ended;
    internal sealed class Flight(CancellationToken shutdown)
    {
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        internal Task<byte[]> Task = null!;
        internal int Consumers;
        internal long Bytes;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal async Task<Lease> AcquireAsync(Uri uri, CancellationToken cancellation)
    {
        Flight flight;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            Task wait;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_pending.TryGetValue(uri.AbsoluteUri, out flight!))
                {
                    if (!flight.Cancellation.IsCancellationRequested) { flight.Consumers++; _shared++; break; }
                }
                else if (_pending.Count < 32)
                {
                    flight = new(shutdown) { Consumers = 1 };
                    _pending.Add(uri.AbsoluteUri, flight); _started++;
                    var pending = flight;
                    flight.Task = Task.Run(() => ReadAsync(uri, pending));
                    break;
                }
                wait = _capacity.Task;
            }
            await wait.WaitAsync(cancellation).ConfigureAwait(false);
        }
        try
        {
            var bytes = await flight.Task.WaitAsync(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return new(this, uri.AbsoluteUri, flight, bytes);
        }
        catch { Release(uri.AbsoluteUri, flight); throw; }
    }
    private async Task<byte[]> ReadAsync(Uri uri, Flight flight)
    {
        var entered = false;
        try
        {
            var token = flight.Cancellation.Token;
            await _downloads.WaitAsync(token).ConfigureAwait(false); entered = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var bytes = await read(uri, timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (flight.Consumers > 0) { flight.Bytes = bytes.LongLength; _bytes += flight.Bytes; _peak = Math.Max(_peak, _bytes); }
            }
            return bytes;
        }
        finally { if (entered) _downloads.Release(); lock (_gate) _ended++; }
    }
    private void Release(string key, Flight flight)
    {
        lock (_gate)
        {
            if (--flight.Consumers != 0) return;
            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _pending.Remove(key);
            _bytes -= flight.Bytes; flight.Bytes = 0;
            flight.Cancellation.Cancel();
            if (flight.Task.IsCompleted) flight.Cancellation.Dispose();
            else _ = flight.Task.ContinueWith(t => { _ = t.Exception; flight.Cancellation.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var signal = _capacity; _capacity = NewSignal(); signal.TrySetResult();
        }
    }
    internal void Reset() { lock (_gate) foreach (var flight in _pending.Values) flight.Cancellation.Cancel(); }
    internal object Snapshot() { lock (_gate) return new { pending = _pending.Count, bytes = _bytes, peakBytes = _peak, shared = _shared, started = _started, ended = _ended }; }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; Reset(); _capacity.TrySetResult(); } }
    internal sealed class Lease : IDisposable
    {
        private SharedImageBodies? _owner;
        private readonly string _key;
        private Flight? _flight;
        private byte[]? _bytes;
        internal byte[] Bytes => _bytes ?? throw new ObjectDisposedException(nameof(Lease));
        internal Lease(SharedImageBodies owner, string key, Flight flight, byte[] bytes) { _owner = owner; _key = key; _flight = flight; _bytes = bytes; }
        public void Dispose() { var owner = Interlocked.Exchange(ref _owner, null); if (owner is null) return; owner.Release(_key, _flight!); _flight = null; _bytes = null; }
    }
}
