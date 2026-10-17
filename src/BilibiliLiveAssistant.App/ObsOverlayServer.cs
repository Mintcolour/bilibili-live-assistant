using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

// Minimal read-only loopback HTTP output. No URL reservations, elevation, web view or account credentials.
internal sealed class ObsOverlayServer : IAsyncDisposable
{
    private sealed class ImageEntry(ObsImageReference value) : IDisposable
    {
        public ObsImageReference Value { get; } = value;
        public CancellationTokenSource Removed { get; } = new();
        public void Dispose() { Removed.Cancel(); Removed.Dispose(); }
    }
    private readonly TcpListener _listener;
    private readonly BiliImageCache _cache;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _imageSlots = new(2, 2);
    private readonly object _gate = new();
    private readonly Dictionary<string, ImageEntry> _images = new();
    private readonly HashSet<Task> _requests = new();
    private TaskCompletionSource<bool> _changed = Signal();
    private byte[] _snapshot = "{}"u8.ToArray();
    private byte[] _theme = "{}"u8.ToArray();
    private long _themeVersion;
    private TaskCompletionSource<bool> _previewChanged = Signal();
    private byte[] _previewSnapshot = "{}"u8.ToArray(), _previewTheme = "{}"u8.ToArray();
    private long _previewVersion;
    private int _previewConsumers;
    private readonly bool _previewOnly;
    private long _version, _imagesServed;
    private int _consumers, _connections;
    private Task? _accept;
    private readonly string _prefix;
    private string _host;
    public string Address => "http://" + _host + _prefix;
    public int ConsumerCount => Volatile.Read(ref _consumers);
    public int PreviewConsumerCount => Volatile.Read(ref _previewConsumers);
    public event Action<int>? ConsumersChanged;
    public event Action<int>? PreviewConsumersChanged;
    public ObsOverlayServer(int port, BiliImageCache cache, bool previewOnly = false)
    {
        _previewOnly = previewOnly; _cache = cache; _listener = new(IPAddress.Loopback, port); _host = $"127.0.0.1:{port}";
        if (previewOnly) { _prefix = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() + "/"; return; }
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BilibiliLiveAssistant");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "obs-source.key");
        var key = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        if (key.Length != 48 || key.Any(c => !Uri.IsHexDigit(c)))
        { key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(); File.WriteAllText(path, key); }
        _prefix = "/" + key + "/";
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Start() { _listener.Start(32); _host = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}"; _accept = AcceptAsync(); }
    public void PublishPreview(AppSettings settings)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(new { width = settings.Compact.ObsWidth, height = settings.Compact.ObsHeight,
            config = ObsOverlayProjection.Configuration(settings.Compact), style = ObsOverlayProjection.Style(settings) }, options);
        var theme = JsonSerializer.SerializeToUtf8Bytes(settings.Compact.ObsTheme, options);
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            _previewSnapshot = snapshot; _previewTheme = theme; _previewVersion++;
            var signal = _previewChanged; _previewChanged = Signal(); signal.TrySetResult(true);
        }
    }
    public void ClearPreview()
    {
        lock (_gate) { _previewSnapshot = "{}"u8.ToArray(); _previewTheme = "{}"u8.ToArray(); }
    }
    public void ClearSnapshot() => Publish(new("{}"u8.ToArray(), new Dictionary<string, ObsImageReference>()));
    public void PublishTheme(ObsThemeSettings theme)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(theme, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || bytes.AsSpan().SequenceEqual(_theme)) return;
            _theme = bytes; _themeVersion++; var signal = _changed; _changed = Signal(); signal.TrySetResult(true);
        }
    }
    internal object GetDiagnostics() => new { connections = Volatile.Read(ref _connections), clients = Volatile.Read(ref _consumers), previewClients = PreviewConsumerCount,
        themeBytes = _theme.Length, themeRevision = Interlocked.Read(ref _themeVersion), imagesServed = Interlocked.Read(ref _imagesServed) };
    public void Publish(ObsOverlayDocument document)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            foreach (var key in _images.Keys.Where(k => !document.Images.ContainsKey(k)).ToArray()) { _images[key].Dispose(); _images.Remove(key); }
            foreach (var item in document.Images) if (!_images.ContainsKey(item.Key)) _images.Add(item.Key, new(item.Value));
            _snapshot = document.Json; _version++; var signal = _changed; _changed = Signal(); signal.TrySetResult(true);
        }
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (Interlocked.Increment(ref _connections) > 32) { Interlocked.Decrement(ref _connections); client.Dispose(); continue; }
                var task = HandleAsync(client);
                lock (_gate) _requests.Add(task);
                _ = task.ContinueWith(t => { lock (_gate) _requests.Remove(t); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            var stream = client.GetStream(); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var bytes = new byte[8192]; var count = 0; var end = -1;
                while (count < bytes.Length && end < 0)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token).ConfigureAwait(false); if (read == 0) return;
                    count += read; end = FindHeaderEnd(bytes, count);
                }
                if (end < 0) return;
                var lines = Encoding.ASCII.GetString(bytes, 0, end).Split("\r\n"); var request = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':'); if (colon <= 0) return;
                    if (!headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim())) return;
                }
                if (request.Length != 3 || request[0] != "GET" || request[1].Length > 2048 || request[2] is not ("HTTP/1.1" or "HTTP/1.0")
                    || headers.GetValueOrDefault("Host") != _host || headers.ContainsKey("Transfer-Encoding")
                    || headers.TryGetValue("Content-Length", out var length) && length != "0"
                    || headers.TryGetValue("Origin", out var origin) && origin != "http://" + _host
                    || headers.GetValueOrDefault("Sec-Fetch-Site") == "cross-site"
                    || !request[1].StartsWith(_prefix, StringComparison.Ordinal))
                { await Reply(stream, 403, "text/plain", "Forbidden"u8.ToArray(), deadline.Token); return; }
                var route = request[1][_prefix.Length..];
                if (route == "preview-events" && _previewOnly) { deadline.CancelAfter(Timeout.InfiniteTimeSpan); await PreviewEvents(stream, deadline.Token); return; }
                if (route == "events" && !_previewOnly) { deadline.CancelAfter(Timeout.InfiniteTimeSpan); await Events(stream, deadline.Token); return; }
                if (route.StartsWith("image/", StringComparison.Ordinal) && !_previewOnly) { deadline.CancelAfter(TimeSpan.FromSeconds(10)); await Image(stream, route[6..], deadline.Token); return; }
                if (route.StartsWith("assets/", StringComparison.Ordinal))
                {
                    await _imageSlots.WaitAsync(deadline.Token);
                    try
                    {
                        var asset = await ObsThemeAssets.ReadAsync(route[7..], deadline.Token);
                        await Reply(stream, asset is null ? 404 : 200, asset?.Type ?? "text/plain", asset?.Bytes ?? [], deadline.Token);
                    }
                    finally { _imageSlots.Release(); }
                    return;
                }
                var resource = route switch
                {
                    "" when !_previewOnly => ("index.html", "text/html; charset=utf-8"),
                    "preview.html" when _previewOnly => ("preview.html", "text/html; charset=utf-8"),
                    "preview.js" when _previewOnly => ("preview.js", "text/javascript; charset=utf-8"),
                    "preview.css" when _previewOnly => ("preview.css", "text/css; charset=utf-8"),
                    "sample-avatar.svg" when _previewOnly => ("sample-avatar.svg", "image/svg+xml"),
                    "sample-emote.svg" when _previewOnly => ("sample-emote.svg", "image/svg+xml"),
                    "overlay.js" => ("overlay.js", "text/javascript; charset=utf-8"), "theme.js" => ("theme.js", "text/javascript; charset=utf-8"),
                    "overlay.css" => ("overlay.css", "text/css; charset=utf-8"), _ => ("", "text/plain"),
                };
                if (resource.Item1.Length == 0) { await Reply(stream, 404, "text/plain", [], deadline.Token); return; }
                using var file = Assembly.GetExecutingAssembly().GetManifestResourceStream("BilibiliLiveAssistant.App.Obs." + resource.Item1) ?? throw new IOException("OBS resource missing");
                using var buffer = new MemoryStream(); await file.CopyToAsync(buffer, deadline.Token);
                await Reply(stream, 200, resource.Item2, buffer.ToArray(), deadline.Token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            finally { Interlocked.Decrement(ref _connections); }
        }
    }
    private async Task PreviewEvents(Stream stream, CancellationToken cancellation)
    {
        if (Interlocked.Increment(ref _previewConsumers) > 1)
        { Interlocked.Decrement(ref _previewConsumers); await Reply(stream, 503, "text/plain", [], cancellation); return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var disconnected = WatchDisconnect(stream, lifetime); cancellation = lifetime.Token;
        try
        {
            PreviewConsumersChanged?.Invoke(1);
            await Header(stream, 200, "text/event-stream", null, cancellation);
            long seen = -1;
            while (!cancellation.IsCancellationRequested)
            {
                byte[] snapshot, theme; long version; Task next;
                lock (_gate) { snapshot = _previewSnapshot; theme = _previewTheme; version = _previewVersion; next = _previewChanged.Task; }
                using var write = CancellationTokenSource.CreateLinkedTokenSource(cancellation); write.CancelAfter(TimeSpan.FromSeconds(10));
                if (seen != version)
                {
                    await stream.WriteAsync("event: theme\ndata: "u8.ToArray(), write.Token); await stream.WriteAsync(theme, write.Token);
                    await stream.WriteAsync("\n\nevent: state\ndata: "u8.ToArray(), write.Token); await stream.WriteAsync(snapshot, write.Token);
                    await stream.WriteAsync("\n\n"u8.ToArray(), write.Token); seen = version;
                }
                else await stream.WriteAsync(": alive\n\n"u8.ToArray(), write.Token);
                await stream.FlushAsync(write.Token);
                try { await next.WaitAsync(TimeSpan.FromSeconds(15), cancellation); } catch (TimeoutException) { }
            }
        }
        finally { lifetime.Cancel(); await disconnected.ConfigureAwait(false); PreviewConsumersChanged?.Invoke(Interlocked.Decrement(ref _previewConsumers)); }
    }
    private static int FindHeaderEnd(byte[] bytes, int count)
    {
        for (var i = 0; i <= count - 4; i++) if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i;
        return -1;
    }
    private async Task Events(Stream stream, CancellationToken cancellation)
    {
        if (Interlocked.Increment(ref _consumers) > 2) { Interlocked.Decrement(ref _consumers); await Reply(stream, 503, "text/plain", [], cancellation); return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var disconnected = WatchDisconnect(stream, lifetime);
        cancellation = lifetime.Token;
        try
        {
            Task initial; lock (_gate) initial = _changed.Task;
            ConsumersChanged?.Invoke(Volatile.Read(ref _consumers));
            // A reconnect must wait for a fresh UI snapshot, never replay a previous room.
            await initial.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            await Header(stream, 200, "text/event-stream", null, cancellation);
            long seen = -1, themeSeen = -1;
            while (!cancellation.IsCancellationRequested)
            {
                byte[] snapshot, theme; long version, themeVersion; Task next;
                lock (_gate) { snapshot = _snapshot; version = _version; next = _changed.Task; theme = _theme; themeVersion = _themeVersion; }
                using var write = CancellationTokenSource.CreateLinkedTokenSource(cancellation); write.CancelAfter(TimeSpan.FromSeconds(10));
                if (themeSeen != themeVersion)
                {
                    await stream.WriteAsync("event: theme\ndata: "u8.ToArray(), write.Token);
                    await stream.WriteAsync(theme, write.Token); await stream.WriteAsync("\n\n"u8.ToArray(), write.Token); themeSeen = themeVersion;
                }
                if (version != seen)
                {
                    await stream.WriteAsync("event: state\ndata: "u8.ToArray(), write.Token);
                    await stream.WriteAsync(snapshot, write.Token); await stream.WriteAsync("\n\n"u8.ToArray(), write.Token); seen = version;
                }
                else await stream.WriteAsync(": alive\n\n"u8.ToArray(), write.Token);
                await stream.FlushAsync(write.Token);
                try { await next.WaitAsync(TimeSpan.FromSeconds(15), cancellation); } catch (TimeoutException) { }
            }
        }
        finally
        {
            lifetime.Cancel(); await disconnected.ConfigureAwait(false);
            ConsumersChanged?.Invoke(Interlocked.Decrement(ref _consumers));
        }
    }
    private static async Task WatchDisconnect(Stream stream, CancellationTokenSource lifetime)
    {
        try { await stream.ReadAsync(new byte[1], lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { lifetime.Cancel(); }
    }
    private async Task Image(Stream stream, string key, CancellationToken cancellation)
    {
        ObsImageReference reference; CancellationToken removed;
        lock (_gate)
        {
            if (!_images.TryGetValue(key, out var entry)) { reference = new("", 0, 64, BiliImageRole.Content); removed = new(true); }
            else { reference = entry.Value; removed = entry.Removed.Token; }
        }
        if (removed.IsCancellationRequested) { await Reply(stream, 404, "text/plain", [], cancellation); return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, removed);
        await _imageSlots.WaitAsync(linked.Token);
        try
        {
            var url = reference.Url;
            if (url.Length == 0 && reference.Uid > 0) url = await _cache.Avatars.ResolveAsync(reference.Uid, null, linked.Token);
            var image = await _cache.GetStaticForViewAsync(url, linked.Token, reference.Limit, reference.Role).ConfigureAwait(false);
            if (image is null) { await Reply(stream, 404, "text/plain", [], linked.Token); return; }
            using var bitmap = image.Frame.CreateDrawingBitmap(); using var buffer = new MemoryStream();
            // PNG exists only at this browser transport boundary. Native consumers keep the pixel pipeline.
            bitmap.Save(buffer, ImageFormat.Png); Interlocked.Increment(ref _imagesServed);
            await Reply(stream, 200, "image/png", buffer.ToArray(), linked.Token);
        }
        finally { _imageSlots.Release(); }
    }
    private static async Task Reply(Stream stream, int status, string type, byte[] body, CancellationToken cancellation)
    { await Header(stream, status, type, body.Length, cancellation); if (body.Length > 0) await stream.WriteAsync(body, cancellation); }
    private static Task Header(Stream stream, int status, string type, int? length, CancellationToken cancellation)
    {
        var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Unavailable")}\r\nContent-Type: {type}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'\r\n";
        if (length is { } size) header += $"Content-Length: {size}\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(header + "\r\n"), cancellation).AsTask();
    }
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel(); _listener.Stop(); lock (_gate) { foreach (var entry in _images.Values) entry.Dispose(); _images.Clear(); _snapshot = []; _theme = []; _previewSnapshot = []; _previewTheme = []; _changed.TrySetCanceled(); _previewChanged.TrySetCanceled(); }
        if (_accept is not null) await _accept.ConfigureAwait(false);
        Task[] requests; lock (_gate) requests = _requests.ToArray();
        try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        ConsumersChanged = null; PreviewConsumersChanged = null;
        _imageSlots.Dispose(); _stop.Dispose();
    }
}
