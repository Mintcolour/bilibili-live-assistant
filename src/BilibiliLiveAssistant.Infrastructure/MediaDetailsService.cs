using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

/// <summary>Local shared metadata requests; no cookies, browser, or unbounded queue.</summary>
public sealed class MediaDetailsService : IDisposable
{
    private readonly object _gate = new();
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requests = new(2, 2);
    private readonly Dictionary<string, Pending> _inflight = new();
    private readonly Dictionary<string, Cached> _cache = new();
    private TaskCompletionSource<bool> _capacity = Signal();
    private long _sequence;
    private int _generation;
    private bool _disposed;
    public object GetDiagnostics()
    { lock (_gate) return new { generation = _generation, cached = _cache.Count, inflight = _inflight.Count }; }
    private sealed record Cached(MediaDetails Details, DateTimeOffset Until, long Used);
    private sealed class Pending
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<MediaDetails> Task { get; set; } = null!;
        public int Consumers;
        public bool Completed;
        public int Generation;
    }
    public MediaDetailsService() : this(null) { }
    public MediaDetailsService(HttpMessageHandler? handler)
    {
        _http = new(handler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BilibiliLiveAssistant/1.0");
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<MediaDetails> GetForViewAsync(MediaReference reference, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var generation = Volatile.Read(ref _generation);
            try { return await GetForGenerationAsync(reference, cancellation, generation).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && generation != Volatile.Read(ref _generation))
            {
                // A historical preview that remains visible is still a valid
                // consumer. Old room controls have already cancelled their token.
            }
        }
    }
    private async Task<MediaDetails> GetForGenerationAsync(MediaReference reference, CancellationToken cancellation, int generation)
    {
        Pending? pending = null;
        while (pending is null)
        {
            cancellation.ThrowIfCancellationRequested();
            Task capacity;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (generation != _generation) throw new OperationCanceledException();
                if (_cache.TryGetValue(reference.Key, out var cached) && cached.Until > DateTimeOffset.UtcNow)
                { _cache[reference.Key] = cached with { Used = ++_sequence }; return cached.Details; }
                _cache.Remove(reference.Key);
                var existing = _inflight.TryGetValue(reference.Key, out pending);
                if (existing && pending!.Cancellation.IsCancellationRequested) pending = null;
                if (!existing && _inflight.Count >= 18) return new(reference, reference.Id, Error: "媒体请求繁忙，暂不加载");
                if (!existing && _inflight.Count < 18)
                {
                    pending = new() { Generation = _generation }; _inflight.Add(reference.Key, pending);
                    var owned = pending;
                    pending.Task = Task.Run(() => LoadAsync(reference, owned));
                }
                if (pending is not null) pending.Consumers++;
                capacity = _capacity.Task;
            }
            if (pending is null) await capacity.WaitAsync(cancellation).ConfigureAwait(false);
        }
        try
        {
            var result = await pending.Task.WaitAsync(cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _generation) throw new OperationCanceledException();
                return result;
            }
        }
        finally
        {
            lock (_gate)
                if (--pending.Consumers == 0)
                {
                    if (pending.Completed) pending.Cancellation.Dispose();
                    else pending.Cancellation.Cancel();
                }
        }
    }
    private async Task<MediaDetails> LoadAsync(MediaReference reference, Pending pending)
    {
        var entered = false;
        try
        {
            var token = pending.Cancellation.Token;
            await _requests.WaitAsync(token).ConfigureAwait(false); entered = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await FetchAsync(reference, timeout.Token).ConfigureAwait(false);
            Store(reference, result, pending); return result;
        }
        catch (OperationCanceledException) when (pending.Cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var result = new MediaDetails(reference, reference.Id, Error: ex is OperationCanceledException ? "加载超时" : "资料暂不可用");
            Store(reference, result, pending); return result;
        }
        finally
        {
            if (entered) _requests.Release();
            lock (_gate)
            {
                if (_inflight.TryGetValue(reference.Key, out var current) && ReferenceEquals(current, pending)) _inflight.Remove(reference.Key);
                pending.Completed = true;
                if (pending.Consumers == 0) pending.Cancellation.Dispose();
                var signal = _capacity; _capacity = Signal(); signal.TrySetResult(true);
            }
        }
    }
    private void Store(MediaReference reference, MediaDetails details, Pending pending)
    {
        lock (_gate)
        {
            if (_disposed || pending.Generation != _generation || pending.Cancellation.IsCancellationRequested) return;
            if (_cache.Count >= 128 && !_cache.ContainsKey(reference.Key)) _cache.Remove(_cache.MinBy(x => x.Value.Used).Key);
            _cache[reference.Key] = new(details, DateTimeOffset.UtcNow.AddMinutes(details.IsSuccess ? 10 : 5), ++_sequence);
        }
    }
    private async Task<MediaDetails> FetchAsync(MediaReference reference, CancellationToken token)
    {
        var valid = reference.Kind == MediaKind.Bilibili ? Regex.IsMatch(reference.Id, @"^BV1[A-Za-z0-9]{9}$") : Regex.IsMatch(reference.Id, @"^[1-9][0-9]{0,9}$");
        if (!valid) return new(reference, reference.Id, Error: "无效媒体编号");
        var url = reference.Kind == MediaKind.Bilibili ? $"https://api.bilibili.com/x/web-interface/view?bvid={reference.Id}"
            : $"https://store.steampowered.com/api/appdetails?appids={reference.Id}&cc=CN&l=schinese";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(reference.Kind == MediaKind.Bilibili ? "https://www.bilibili.com/" : "https://store.steampowered.com/");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new(reference, reference.Id, Error: $"资料请求失败（{(int)response.StatusCode}）");
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Metadata size limit");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
        { if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("Metadata size limit"); buffer.Write(bytes, 0, count); }
        using var json = JsonDocument.Parse(buffer.ToArray()); var root = json.RootElement;
        if (reference.Kind == MediaKind.Bilibili)
        {
            var code = Get(root, "code");
            if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var status) || status != 0) return new(reference, reference.Id, Error: "视频不可访问");
            var data = Get(root, "data"); var owner = Get(data, "owner"); var views = Get(Get(data, "stat"), "view");
            var summary = views.ValueKind == JsonValueKind.Number && views.TryGetInt64(out var number) ? $"播放 {number.ToString("N0", CultureInfo.InvariantCulture)}" : "播放量暂无数据";
            return new(reference, Text(data, "title"), Text(data, "pic"), Text(owner, "name"), Text(owner, "face"), summary);
        }
        var app = Get(root, reference.Id);
        if (Get(app, "success").ValueKind != JsonValueKind.True) return new(reference, reference.Id, Error: "游戏不存在或当前地区不可用");
        var game = Get(app, "data"); var developers = Get(game, "developers");
        var developer = developers.ValueKind == JsonValueKind.Array ? string.Join("、", developers.EnumerateArray().Take(4).Select(x => x.GetString())) : "开发商暂无数据";
        var price = Get(game, "is_free").ValueKind == JsonValueKind.True ? "免费" : Text(Get(game, "price_overview"), "final_formatted");
        return new(reference, Text(game, "name"), Text(game, "header_image"), developer.Length > 512 ? developer[..512] : developer,
            Summary: price.Length > 0 ? price : "价格暂无数据");
    }
    private static JsonElement Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;
    public void ResetRoom()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _generation++; _cache.Clear();
            foreach (var pending in _inflight.Values) pending.Cancellation.Cancel();
            var signal = _capacity; _capacity = Signal(); signal.TrySetResult(true);
        }
    }
    private static string Text(JsonElement value, string key)
    { var item = Get(value, key); var text = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : ""; return text.Length > 512 ? text[..512] : text; }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var pending in _inflight.Values) pending.Cancellation.Cancel();
            _cache.Clear(); _capacity.TrySetResult(true);
        }
        _http.Dispose();
    }
}
