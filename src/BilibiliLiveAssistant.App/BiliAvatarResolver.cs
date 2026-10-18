using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace BilibiliLiveAssistant.App;

// Only visible consumers request missing avatars. No nicknames, account cookies,
// timer, persisted profile database or per-message background lookup queue.
internal sealed class BiliAvatarResolver : IDisposable
{
    private const int CacheLimit = 512, PendingLimit = 16;
    private readonly object _gate = new();
    private readonly Dictionary<long, Cached> _cache = new();
    private readonly LinkedList<long> _lru = new();
    private readonly Dictionary<long, Pending> _pending = new();
    private readonly SemaphoreSlim _slots = new(2, 2), _startGate = new(1, 1);
    private readonly HttpClient _http = new(new HttpClientHandler
    { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
    { Timeout = TimeSpan.FromSeconds(8) };
    private TaskCompletionSource _capacity = NewSignal();
    private long _nextStart, _hits, _requests, _failures, _cancelled, _observed, _unidentified;
    private DateTimeOffset _cooldown;
    private int _generation, _active;
    private bool _disposed;
    private sealed class Cached(string url, DateTimeOffset until, LinkedListNode<long> node)
    {
        public string Url = url;
        public DateTimeOffset Until = until;
        public LinkedListNode<long> Node = node;
    }
    private sealed class Pending(int generation)
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly int Generation = generation;
        public Task<string> Task = null!;
        public int Consumers;
        public bool Completed;
    }
    internal BiliAvatarResolver()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BilibiliLiveAssistant/1.0");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com/");
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string ValidUrl(string? value)
    {
        if (BiliImageCache.NormalizeUrl(value) is not { } uri) return "";
        var host = uri.DnsSafeHost;
        return host is "hdslb.com" or "biliimg.com" || host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".biliimg.com", StringComparison.OrdinalIgnoreCase) ? uri.AbsoluteUri : "";
    }
    internal void Observe(long uid, string? url)
    {
        if (uid <= 0 || string.IsNullOrEmpty(url)) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (_cache.TryGetValue(uid, out var cached) && cached.Url == url)
            { cached.Until = DateTimeOffset.UtcNow.AddMinutes(10); Touch(cached); _observed++; return; }
        }
        if (ValidUrl(url) is not { Length: > 0 } valid) return;
        lock (_gate)
        {
            if (_disposed) return;
            Put(uid, valid); _observed++;
        }
    }
    private void Put(long uid, string url)
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(url.Length > 0 ? 10 : 5);
        if (_cache.TryGetValue(uid, out var cached))
        { cached.Url = url; cached.Until = until; Touch(cached); return; }
        if (_cache.Count >= CacheLimit && _lru.First is { } oldest) { _cache.Remove(oldest.Value); _lru.RemoveFirst(); }
        _cache.Add(uid, new(url, until, _lru.AddLast(uid)));
    }
    private void Touch(Cached cached) { _lru.Remove(cached.Node); _lru.AddLast(cached.Node); }
    internal async Task<string> ResolveAsync(long uid, string? suppliedUrl, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var supplied = ValidUrl(suppliedUrl);
        if (supplied.Length > 0) { Observe(uid, supplied); return supplied; }
        if (uid <= 0) { Interlocked.Increment(ref _unidentified); return ""; }
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            Pending? pending; Task capacity;
            lock (_gate)
            {
                if (_disposed) return "";
                if (_cache.TryGetValue(uid, out var cached) && cached.Until > DateTimeOffset.UtcNow)
                { Touch(cached); _hits++; return cached.Url; }
#if MEMORY_SOAK
                if (App.Replay is not null) return ""; // Offline replay never fills gaps with network lookups.
#endif
                if (_cooldown > DateTimeOffset.UtcNow) return "";
                if (_pending.TryGetValue(uid, out pending) && pending.Cancellation.IsCancellationRequested) pending = null;
                else if (pending is null && _pending.Count < PendingLimit)
                {
                    pending = new(_generation);
                    _pending.Add(uid, pending);
                    var work = pending;
                    pending.Task = Task.Run(() => LoadAsync(uid, work));
                }
                if (pending is not null) pending.Consumers++;
                capacity = _capacity.Task;
            }
            if (pending is null) { await capacity.WaitAsync(cancellation).ConfigureAwait(false); continue; }
            try { return await pending.Task.WaitAsync(cancellation).ConfigureAwait(false); }
            finally
            {
                lock (_gate)
                    if (--pending.Consumers == 0)
                    {
                        if (pending.Completed) pending.Cancellation.Dispose();
                        else { _cancelled++; pending.Cancellation.Cancel(); }
                    }
            }
        }
    }
    private async Task<string> LoadAsync(long uid, Pending pending)
    {
        var entered = false; var token = pending.Cancellation.Token;
        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false); entered = true;
            Interlocked.Increment(ref _active);
            await _startGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var delay = (_nextStart - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
                if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                _nextStart = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            }
            finally { _startGate.Release(); }
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || pending.Generation != _generation || _cooldown > DateTimeOffset.UtcNow) return "";
                if (_cache.TryGetValue(uid, out var known) && known.Until > DateTimeOffset.UtcNow && known.Url.Length > 0) return known.Url;
                _requests++;
            }
            // Fixed, public endpoint. Identity is checked before accepting its face.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _http.GetAsync("https://api.bilibili.com/x/web-interface/card?mid="
                + uid.ToString(CultureInfo.InvariantCulture) + "&photo=false", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PreconditionFailed or HttpStatusCode.Forbidden)
                Cooldown();
            response.EnsureSuccessStatusCode();
            using var document = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var result) && result.TryGetInt32(out var number) ? number : -1;
            if (code is -412 or -352 or -509) Cooldown();
            var url = "";
            if (code == 0 && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object
                && card.TryGetProperty("mid", out var mid) && long.TryParse(mid.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var returnedUid)
                && returnedUid == uid && card.TryGetProperty("face", out var face) && face.ValueKind == JsonValueKind.String)
                url = ValidUrl(face.GetString());
            return Complete(uid, pending, url);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return ""; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Complete(uid, pending, ""); }
        finally
        {
            if (entered) { Interlocked.Decrement(ref _active); _slots.Release(); }
            lock (_gate)
            {
                if (_pending.TryGetValue(uid, out var current) && ReferenceEquals(current, pending)) _pending.Remove(uid);
                pending.Completed = true;
                if (pending.Consumers == 0) pending.Cancellation.Dispose();
                var signal = _capacity; _capacity = NewSignal(); signal.TrySetResult();
            }
        }
    }
    private string Complete(long uid, Pending pending, string url)
    {
        lock (_gate)
        {
            if (_disposed || pending.Generation != _generation || pending.Cancellation.IsCancellationRequested) return "";
            // A newer event may have supplied the actual avatar while HTTP was in flight.
            if (_cache.TryGetValue(uid, out var known) && known.Url.Length > 0 && known.Until > DateTimeOffset.UtcNow) return known.Url;
            Put(uid, url); if (url.Length == 0) _failures++;
            return url;
        }
    }
    private void Cooldown() { lock (_gate) _cooldown = DateTimeOffset.UtcNow.AddMinutes(1); }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int limit = 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("头像资料响应过大");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > limit) throw new InvalidDataException("头像资料响应过大");
            output.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(output.GetBuffer().AsMemory(0, checked((int)output.Length)), new JsonDocumentOptions { MaxDepth = 24 });
    }
    internal void Reset()
    {
        lock (_gate)
        {
            _generation++; _cache.Clear(); _lru.Clear();
            foreach (var pending in _pending.Values) pending.Cancellation.Cancel();
            var signal = _capacity; _capacity = NewSignal(); signal.TrySetResult();
        }
    }
    internal object GetDiagnostics()
    {
        lock (_gate) return new { cached = _cache.Count, pending = _pending.Count, active = Volatile.Read(ref _active),
            hits = _hits, requests = _requests, failures = _failures, cancelled = _cancelled, observed = _observed,
            unidentified = Interlocked.Read(ref _unidentified), coolingDown = _cooldown > DateTimeOffset.UtcNow };
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Reset(); }
        _http.Dispose();
    }
}
