using Microsoft.UI.Dispatching;

namespace BilibiliLiveAssistant.App;

// Every dictionary, lease and native source is owned by the main UI thread.
// Pending consumers share one factory; idle and leased sources are disjoint.
internal sealed class UiImageSourceService : IDisposable
{
    private readonly BiliImageCache _images;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<Key, Entry> _sources = new();
    private readonly Dictionary<Key, Pending> _pending = new();
    private readonly List<KeyValuePair<Key, Entry>> _idleSort = new(288);
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly DispatcherQueueTimer _idleTimer;
    private TaskCompletionSource _capacity = NewSignal();
    private long _sequence, _cancelled, _aborted, _stale, _hits, _created, _released, _handoffJoins;
    private bool _disposed;
    private int _generation;
    private long _idleByteLimit = 8 * 1024 * 1024;
    private int _idleItemLimit = 256;
    internal Action? ResourceActivity { get; set; }
    internal readonly record struct Key(string Url, int Width, int Limit, int Generation, BiliImageRole Role);
    internal sealed class Entry(UiPixelSource value) { public UiPixelSource Value = value; public int Leases; public long Used; }
    private sealed class Pending
    {
        public readonly CancellationTokenSource Cancel = new();
        public readonly TaskCompletionSource<Entry?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters;
        public bool Started, Finished, CancelQueued;
    }
    internal UiImageSourceService(BiliImageCache images)
    {
        _images = images; _idleTimer = _dispatcher.CreateTimer();
        _idleTimer.Interval = TimeSpan.FromSeconds(15); _idleTimer.IsRepeating = false;
        _idleTimer.Tick += (_, _) => { if (!HasConsumers) ClearIdle(); };
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool HasConsumers => _pending.Count > 0 || _sources.Values.Any(e => e.Leases > 0);
    private void CheckThread() { if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("图片显示资源必须由 UI 线程管理"); }

    internal async Task<Lease?> AcquireStaticSourceAsync(string url, int width, int limit, CancellationToken cancellation, BiliImageRole role = BiliImageRole.Content)
    {
        CheckThread();
        ResourceActivity?.Invoke();
        if (BiliImageCache.NormalizeUrl(url) is not { } uri) return null;
        var key = new Key(uri.AbsoluteUri, Math.Clamp(width, 1, 256), BiliImageCache.SizeTier(limit), _generation, role);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_disposed) return null;
            if (key.Generation != _generation) key = key with { Generation = _generation };
            _idleTimer.Stop();
            if (_sources.TryGetValue(key, out var cached)) { _hits++; return Borrow(key, cached); }
            if (_pending.TryGetValue(key, out var pending) && pending.Cancel.IsCancellationRequested)
            { await _capacity.Task.WaitAsync(cancellation); continue; }
            if (pending is null)
            {
                if (_pending.Count >= 32) { await _capacity.Task.WaitAsync(cancellation); continue; }
                pending = new(); _pending.Add(key, pending);
                // Acquire a consumer before the factory can complete synchronously.
                pending.Waiters++;
                _ = CreateAsync(key, pending);
            }
            else
            {
                pending.Waiters++;
                if (pending.CancelQueued) _handoffJoins++;
            }
            try
            {
                var entry = await pending.Completion.Task.WaitAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (_disposed) return null;
                if (key.Generation != _generation) continue;
                if (entry is null) return null;
                return Borrow(key, entry);
            }
            finally
            {
                if (--pending.Waiters == 0)
                {
                    if (pending.Finished) { _pending.Remove(key); pending.Cancel.Dispose(); SignalCapacity(); TrimIdle(); }
                    else ScheduleCancellation(key, pending);
                }
                ScheduleIdle();
            }
        }
    }
    private Lease Borrow(Key key, Entry entry) { entry.Leases++; entry.Used = ++_sequence; _idleTimer.Stop(); return new(this, key, entry); }
    private void ScheduleCancellation(Key key, Pending pending)
    {
        if (pending.CancelQueued) return;
        pending.CancelQueued = true;
        // Stable row slots hand an image to the next visible row in the same UI
        // batch. Give that row one dispatcher turn to join the existing factory,
        // rather than aborting and restarting a slow body for every slot shift.
        // At most one callback per pending key (32); no timer or idle polling.
        if (_dispatcher.TryEnqueue(() =>
        {
            pending.CancelQueued = false;
            if (_disposed || pending.Finished || !ReferenceEquals(_pending.GetValueOrDefault(key), pending) || pending.Waiters > 0) return;
            _cancelled++; pending.Cancel.Cancel();
        })) return;
        pending.CancelQueued = false;
        _cancelled++; pending.Cancel.Cancel();
    }
    private async Task CreateAsync(Key key, Pending pending)
    {
        var entered = false; UiPixelSource? value = null;
        try
        {
            var token = pending.Cancel.Token;
            await _slots.WaitAsync(token); entered = true; pending.Started = true;
            var asset = await _images.GetStaticForViewAsync(key.Url, token, key.Limit, key.Role);
            if (asset is null) { pending.Completion.TrySetResult(null); return; }
            value = await UiPixelSource.CreateAsync(asset.Frame, key.Width, asset.CanAnimate, asset.DecodeLimit, token);
            if (_disposed || key.Generation != _generation || token.IsCancellationRequested || pending.Waiters == 0)
            { _stale++; pending.Completion.TrySetResult(null); return; }
            var entry = new Entry(value) { Used = ++_sequence };
            _sources.Add(key, entry); _created++; value = null;
            pending.Completion.TrySetResult(entry);
        }
        catch (OperationCanceledException) { _aborted++; pending.Completion.TrySetResult(null); }
        catch (Exception ex)
        {
            if (ex is OutOfMemoryException) pending.Completion.TrySetException(ex);
            else { BiliImageDiagnostics.Record(key.Url, "ui-pixel-source", ex); pending.Completion.TrySetResult(null); }
        }
        finally
        {
            value?.Dispose();
            if (entered) _slots.Release();
            pending.Finished = true;
            if (pending.Waiters == 0) { _pending.Remove(key); pending.Cancel.Dispose(); }
            SignalCapacity(); TrimIdle(); ScheduleIdle();
            ResourceActivity?.Invoke();
        }
    }
    private void SignalCapacity() { var signal = _capacity; _capacity = NewSignal(); signal.TrySetResult(); }
    private void Release(Key key, Entry entry)
    {
        CheckThread();
        if (--entry.Leases != 0) return;
        entry.Used = ++_sequence;
        if (_disposed || key.Generation != _generation) Remove(key, entry);
        else TrimIdle();
        ScheduleIdle();
        ResourceActivity?.Invoke();
    }
    private void TrimIdle()
    {
        if (_disposed) { ClearIdle(); return; }
        // Common case: a lease release leaves the idle set inside both budgets.
        // Scan once and return before sorting, instead of sorting every release.
        long estimated = 0; var idleCount = 0; var stale = false;
        foreach (var pair in _sources)
        {
            if (pair.Value.Leases != 0 || _pending.ContainsKey(pair.Key)) continue;
            idleCount++; estimated += pair.Value.Value.Bytes;
            if (pair.Key.Generation != _generation) stale = true;
        }
        if (!stale && idleCount <= _idleItemLimit && estimated <= _idleByteLimit) return;
        // A completed factory's waiters have not yet received their leases.
        // This runs for every borrow completion/release. Reuse one bounded sort
        // buffer instead of allocating a dictionary-sized LINQ array each time.
        _idleSort.Clear();
        foreach (var pair in _sources)
            if (pair.Value.Leases == 0 && !_pending.ContainsKey(pair.Key)) _idleSort.Add(pair);
        _idleSort.Sort(static (a, b) => b.Value.Used.CompareTo(a.Value.Used));
        // Charge both the native input bitmap and one estimated display surface.
        long bytes = 0; var count = 0;
        foreach (var pair in _idleSort)
        {
            if (pair.Key.Generation == _generation && ++count <= _idleItemLimit && bytes + pair.Value.Value.Bytes <= _idleByteLimit) bytes += pair.Value.Value.Bytes;
            else Remove(pair.Key, pair.Value);
        }
        _idleSort.Clear();
    }
    internal void SetIdleBudget(long bytes, int count)
    {
        CheckThread();
        if (_disposed || (_idleByteLimit == bytes && _idleItemLimit == count)) return;
        _idleByteLimit = bytes; _idleItemLimit = count; TrimIdle();
    }
    private void ScheduleIdle()
    { if (!_disposed && !HasConsumers && _sources.Count > 0 && !_idleTimer.IsRunning) _idleTimer.Start(); }
    internal void ClearIdle()
    {
        CheckThread(); _idleTimer.Stop();
        foreach (var pair in _sources.Where(p => p.Value.Leases == 0 && !_pending.ContainsKey(p.Key)).ToArray())
            Remove(pair.Key, pair.Value);
    }
    internal void ResetRoom()
    {
        CheckThread(); if (_disposed) return;
        _generation++;
        foreach (var pending in _pending.Values) pending.Cancel.Cancel();
        ClearIdle(); SignalCapacity();
        // Other pages may still have bound sources. Their last lease disposes
        // the old generation instead of destroying an image while it is bound.
    }
    private void Remove(Key key, Entry entry) { _sources.Remove(key); entry.Value.Dispose(); _released++; }
    internal object GetDiagnostics()
    {
        CheckThread();
        long idleBytes = 0, activeBytes = 0, reservedBytes = 0;
        var idle = 0; var active = 0; var reserved = 0;
        foreach (var pair in _sources)
        {
            if (pair.Value.Leases > 0) { active++; activeBytes += pair.Value.Value.Bytes; }
            else if (_pending.ContainsKey(pair.Key)) { reserved++; reservedBytes += pair.Value.Value.Bytes; }
            else { idle++; idleBytes += pair.Value.Value.Bytes; }
        }
        return new
        {
            generation = _generation, sources = _sources.Count, leases = _sources.Values.Sum(e => e.Leases),
            retiredSources = _sources.Count(p => p.Key.Generation != _generation),
            retiredLeases = _sources.Where(p => p.Key.Generation != _generation).Sum(p => p.Value.Leases),
            idleSources = idle, activeSources = active, reservedSources = reserved,
            idleBytes, activeBytes, reservedBytes,
            idleBudgetBytes = _idleByteLimit, idleBudgetItems = _idleItemLimit,
            pending = _pending.Count, queued = _pending.Values.Count(p => !p.Started && !p.Finished),
            creating = _pending.Values.Count(p => p.Started && !p.Finished), waiters = _pending.Values.Sum(p => p.Waiters),
            hits = _hits, cancelled = _cancelled, aborted = _aborted, stale = _stale, created = _created, released = _released,
            handoffJoins = _handoffJoins,
        };
    }
    public void Dispose()
    {
        CheckThread(); if (_disposed) return; _disposed = true; _idleTimer.Stop();
        foreach (var pending in _pending.Values) pending.Cancel.Cancel();
        ClearIdle(); SignalCapacity();
        // Leased sources are disposed after their controls detach, not while bound.
    }
    internal sealed class Lease : IDisposable
    {
        private UiImageSourceService? _owner;
        private readonly Key _key;
        private readonly Entry _entry;
        internal UiPixelSource Value => _entry.Value;
        internal Lease(UiImageSourceService owner, Key key, Entry entry) { _owner = owner; _key = key; _entry = entry; }
        public void Dispose() { var owner = _owner; if (owner is null) return; _owner = null; owner.Release(_key, _entry); }
    }
}
