using System.Security.Cryptography;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BilibiliLiveAssistant.App;

internal abstract record BiliImageAsset(int Width, int Height, bool CanAnimate, int DecodeLimit)
{
    public abstract long Size { get; }
}
internal sealed record BiliStaticImageAsset(BiliPixelFrame Frame, bool Animated, int Limit)
    : BiliImageAsset(Frame.Width, Frame.Height, Animated, Limit)
{ public override long Size => Frame.Bytes; }
internal sealed record BiliAnimationAsset(byte[][] Frames, int[] Delays, int W, int H, bool Animated, int Limit, BiliPixelFrame FirstFrame)
    : BiliImageAsset(W, H, Animated, Limit)
{ public override long Size => Frames.Sum(frame => (long)frame.Length) + (long)Width * Height * 4 * Frames.Length; }

/// <summary>Bounded static pixels / compact animation frames. No account lookups or cookies.</summary>
internal sealed class BiliImageCache : IDisposable
{
    internal UiImageSourceService Sources { get; }
    internal BiliAvatarResolver Avatars { get; } = new();
    private static readonly (string Host, string Suffix)[] AllowedHosts =
    [
        ("hdslb.com", ".hdslb.com"), ("biliimg.com", ".biliimg.com"), ("bilivideo.com", ".bilivideo.com"),
        ("steamstatic.com", ".steamstatic.com"), ("steamusercontent.com", ".steamusercontent.com"),
    ];
    private const int DownloadLimit = 2 * 1024 * 1024;
    private const long MemoryLimit = 24 * 1024 * 1024;
    private const int MemoryItemLimit = 1024;
    private const long DiskLimit = 128 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, (BiliImageAsset Asset, long Used)> _memory = new();
    private readonly Dictionary<string, Task<BiliImageAsset?>> _inflight = new();
    private readonly Dictionary<string, DownloadLease> _leases = new();
    private sealed class DownloadLease(CancellationToken shutdown)
    {
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        public int Consumers;
        public bool Completed;
    }
    private readonly Dictionary<string, DateTimeOffset> _failures = new();
    private TaskCompletionSource<bool> _capacityChanged = NewCapacitySignal();
    private readonly SemaphoreSlim _downloads = new(2, 2);
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _http;
    private readonly string _directory;
    private long _bytes;
    private int _generation;
    private long _useSequence;
    private Dictionary<string, (long Length, LinkedListNode<string> Node)>? _diskIndex;
    private readonly LinkedList<string> _diskOrder = new();
    private DateTime _nextDiskScan;
    private long _diskScans, _diskEvictions;
    private long _diskSize;
    private int _diskCount;
    private bool _disposed;
    private long _hits, _loads, _evicted, _loadFailures, _frameHits, _frameMisses, _diskHits, _networkRequests;
    internal object GetDiagnostics()
    {
        lock (_gate) return new { items = _memory.Count, estimatedBytes = _bytes, inflight = _inflight.Count,
            leases = _leases.Count, failureEntries = _failures.Count, hits = _hits, loads = _loads,
            evicted = _evicted, loadFailures = _loadFailures, frameHits = _frameHits, frameMisses = _frameMisses,
            diskHits = Interlocked.Read(ref _diskHits), networkRequests = Interlocked.Read(ref _networkRequests),
            diskScans = Interlocked.Read(ref _diskScans), diskEvictions = Interlocked.Read(ref _diskEvictions),
            staticPixelBytes = _memory.Values.Sum(v => v.Asset is BiliStaticImageAsset s ? s.Frame.Bytes : 0),
            animationEncodedBytes = _memory.Values.Sum(v => v.Asset is BiliAnimationAsset a ? a.Frames.Sum(f => (long)f.Length) : 0),
            animationPixelBytes = _memory.Values.Sum(v => v.Asset is BiliAnimationAsset a ? a.FirstFrame.Bytes : 0),
            generation = _generation, pipeline = "pixel-static-v1" };
    }

    public BiliImageCache(HttpMessageHandler? handler = null, string? directory = null)
    {
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BilibiliLiveAssistant", "image-cache");
        Sources = new(this);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BilibiliLiveAssistant/1.0");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com/");
    }

    private static TaskCompletionSource<bool> NewCapacitySignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async Task<BiliStaticImageAsset?> GetStaticForViewAsync(string url, CancellationToken cancellation, int maxDimension = 256)
        => await GetForViewAsync(url, false, cancellation, maxDimension).ConfigureAwait(false) as BiliStaticImageAsset;
    internal async Task<BiliAnimationAsset?> GetAnimationForViewAsync(string url, CancellationToken cancellation, int maxDimension = 256)
        => await GetForViewAsync(url, true, cancellation, maxDimension).ConfigureAwait(false) as BiliAnimationAsset;

    // Visible views wait for admission instead of mistaking a full queue for a
    // failed image. Unloading cancels only that view's wait, never a shared load.
    internal static int SizeTier(double size) => size <= 64 ? 64 : size <= 128 ? 128 : 256;
    private static string CacheKey(Uri uri, bool animate, int size) => uri.AbsoluteUri + (animate ? "|animated:" : "|static:") + size;

    private async Task<BiliImageAsset?> GetForViewAsync(string url, bool animate, CancellationToken cancellation, int maxDimension)
    {
        if (NormalizeUrl(url) is not { } uri) return null;
        maxDimension = SizeTier(maxDimension);
        var key = CacheKey(uri, animate, maxDimension);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int generation;
            Task<BiliImageAsset?>? pending;
            DownloadLease? lease;
            Task capacityChanged;
            lock (_gate)
            {
                generation = _generation;
                pending = GetOrStart(key, uri, animate, maxDimension, out lease);
                if (lease is not null) lease.Consumers++;
                capacityChanged = _capacityChanged.Task;
            }
            if (pending is not null)
            {
                try
                {
                    var result = await pending.WaitAsync(cancellation).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (generation == _generation) return result;
                    }
                    // A still-visible history/settings consumer may outlive a
                    // room switch. Only that consumer retries in the new scope.
                    continue;
                }
                finally
                {
                    if (lease is not null)
                        lock (_gate)
                            if (--lease.Consumers == 0)
                            { if (lease.Completed) lease.Cancellation.Dispose(); else lease.Cancellation.Cancel(); }
                }
            }
            await capacityChanged.WaitAsync(cancellation).ConfigureAwait(false);
        }
    }

    // Caller owns _gate. Null means temporary capacity pressure; an actual
    // failure/disposal returns a completed task so it cannot cause a retry loop.
    private Task<BiliImageAsset?>? GetOrStart(string key, Uri uri, bool animate, int maxDimension, out DownloadLease? lease)
    {
        lease = null;
        if (_disposed) return Task.FromResult<BiliImageAsset?>(null);
        // A larger cached/in-flight decode also satisfies a smaller consumer.
        for (var size = maxDimension; size <= 256; size *= 2)
        {
            var candidate = CacheKey(uri, animate, size);
            if (_memory.TryGetValue(candidate, out var entry))
            { _hits++; _memory[candidate] = (entry.Asset, ++_useSequence); return Task.FromResult<BiliImageAsset?>(entry.Asset); }
        }
        for (var size = maxDimension; size <= 256; size *= 2)
        {
            var candidate = CacheKey(uri, animate, size);
            if (!_inflight.TryGetValue(candidate, out var pending)) continue;
            if (_leases[candidate].Cancellation.IsCancellationRequested) return null;
            lease = _leases[candidate]; return pending;
        }
        if (_failures.TryGetValue(key, out var failed) && failed > DateTimeOffset.UtcNow)
            return Task.FromResult<BiliImageAsset?>(null);
        if (_inflight.Count >= 32) return null;
        lease = new DownloadLease(_shutdown.Token); _leases.Add(key, lease);
        var cancellation = lease.Cancellation.Token;
        _loads++;
        var task = Task.Run(() => LoadAsync(key, uri, animate, maxDimension, cancellation));
        _inflight.Add(key, task);
        return task;
    }

    internal BiliPixelFrame? TryGetStaticFrame(string url, int maxDimension = 256)
    {
        if (NormalizeUrl(url) is not { } uri) return null;
        lock (_gate)
        {
            for (var size = SizeTier(maxDimension); size <= 256; size *= 2)
            {
                if (!TryFrame(CacheKey(uri, false, size), out var frame) && !TryFrame(CacheKey(uri, true, size), out frame)) continue;
                _frameHits++; return frame;
            }
            _frameMisses++;
            return null;
        }
        bool TryFrame(string key, out BiliPixelFrame? frame)
        {
            frame = null;
            if (!_memory.TryGetValue(key, out var entry)) return false;
            _memory[key] = (entry.Asset, ++_useSequence);
            frame = entry.Asset is BiliStaticImageAsset s ? s.Frame : ((BiliAnimationAsset)entry.Asset).FirstFrame; return true;
        }
    }

    // Keep the bounded disk cache, but do not retain the previous room's decoded
    // pixels or let an old download repopulate memory after its consumers leave.
    internal void ResetRoom()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _memory.Clear(); _failures.Clear(); _bytes = 0;
            foreach (var lease in _leases.Values) lease.Cancellation.Cancel();
            var signal = _capacityChanged; _capacityChanged = NewCapacitySignal(); signal.TrySetResult(true);
        }
        Avatars.Reset();
        Sources.ResetRoom();
    }

    internal static Uri? NormalizeUrl(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048) return null;
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var host = uri.DnsSafeHost;
        var allowed = host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase);
        foreach (var domain in AllowedHosts)
        {
            if (!host.Equals(domain.Host, StringComparison.OrdinalIgnoreCase) && !host.EndsWith(domain.Suffix, StringComparison.OrdinalIgnoreCase)) continue;
            allowed = true; break;
        }
        if (!allowed) return null;
        // Typical CDN URLs are already canonical. Keep the same whitelist,
        // HTTPS upgrade, default port and fragment policy for all other inputs.
        if (uri.Scheme == "https" && uri.IsDefaultPort && uri.Fragment.Length == 0) return uri;
        return new UriBuilder(uri) { Scheme = "https", Port = -1, Fragment = "" }.Uri;
    }

    private async Task<BiliImageAsset?> LoadAsync(string key, Uri uri, bool animate, int maxDimension, CancellationToken token)
    {
        var entered = false;
        var stage = "download";
        byte[]? diagnosticData = null;
        try
        {
            await _downloads.WaitAsync(token).ConfigureAwait(false); entered = true;
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var raw = await ReadOrDownloadAsync(uri, requestTimeout.Token).ConfigureAwait(false);
            diagnosticData = raw;
            BiliImageAsset asset;
            stage = "asset-decode";
            try { asset = await DecodeAsync(raw, animate, maxDimension, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
            {
                try { File.Delete(DiskPath(uri)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            lock (_gate)
            {
                if (_disposed || token.IsCancellationRequested) return null;
                var assetSize = asset.Size;
                while ((_bytes + assetSize > MemoryLimit || _memory.Count >= MemoryItemLimit) && _memory.Count > 0)
                {
                    var oldest = _memory.MinBy(item => item.Value.Used);
                    _bytes -= oldest.Value.Asset.Size; _memory.Remove(oldest.Key); _evicted++;
                }
                if (assetSize <= MemoryLimit) { _memory[key] = (asset, ++_useSequence); _bytes += assetSize; }
            }
            return asset;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException || !token.IsCancellationRequested)
                BiliImageDiagnostics.Record(uri.AbsoluteUri, stage, ex, diagnosticData);
            lock (_gate)
            {
                if (_disposed || token.IsCancellationRequested) return null;
                if (_failures.Count >= 256) _failures.Remove(_failures.MinBy(x => x.Value).Key);
                if (ex is not OperationCanceledException || !token.IsCancellationRequested)
                { _loadFailures++; _failures[key] = DateTimeOffset.UtcNow.AddMinutes(5); }
            }
            return null;
        }
        finally
        {
            if (entered) _downloads.Release();
            lock (_gate)
            {
                _inflight.Remove(key);
                if (_leases.Remove(key, out var lease)) { lease.Completed = true; if (lease.Consumers == 0) lease.Cancellation.Dispose(); }
                var signal = _capacityChanged;
                _capacityChanged = NewCapacitySignal();
                signal.TrySetResult(true);
            }
        }
    }

    private async Task<byte[]> ReadOrDownloadAsync(Uri uri, CancellationToken token)
    {
        var path = DiskPath(uri);
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length is > 0 and <= DownloadLimit)
            { var cached = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false); Interlocked.Increment(ref _diskHits); return cached; }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        byte[]? data = null;
        var current = uri;
        for (var redirect = 0; redirect < 4; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (current.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) || current.Host.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase)
                || current.Host is "steamstatic.com" or "steamusercontent.com" or "steamcdn-a.akamaihd.net")
                request.Headers.Referrer = new Uri("https://store.steampowered.com/");
            Interlocked.Increment(ref _networkRequests);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                current = NormalizeUrl(new Uri(current, location).AbsoluteUri) ?? throw new InvalidDataException("图片跳转地址无效");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > DownloadLimit) throw new InvalidDataException("图片超过大小限制");
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > DownloadLimit) throw new InvalidDataException("图片超过大小限制");
                output.Write(buffer, 0, count);
            }
            data = output.ToArray(); break;
        }
        if (data is null || data.Length == 0) throw new InvalidDataException("无效图片");
        await _diskGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            if (_diskIndex is null || DateTime.UtcNow >= _nextDiskScan) LoadDiskIndex();
            // Each key is written once under the disk gate; bounded reads reject an interrupted write.
            await File.WriteAllBytesAsync(path, data, token).ConfigureAwait(false);
            if (_diskIndex!.Remove(path, out var previous))
            { _diskSize -= previous.Length; _diskOrder.Remove(previous.Node); }
            _diskIndex.Add(path, (data.Length, _diskOrder.AddLast(path)));
            _diskSize += data.Length; _diskCount = _diskIndex.Count;
            TrimDisk();
        }
        catch (IOException) { _diskIndex = null; _diskOrder.Clear(); }
        catch (UnauthorizedAccessException) { _diskIndex = null; _diskOrder.Clear(); }
        finally { _diskGate.Release(); }
        return data;
    }

    private string DiskPath(Uri uri) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))) + ".img");

    // Serialized by _diskGate. Enumerate once, then reconcile at most once per
    // minute for external changes. Normal insertion/eviction touches one file,
    // rather than allocating 512 FileInfo objects and paths for every new image.
    private void LoadDiskIndex()
    {
        var files = new DirectoryInfo(_directory).EnumerateFiles("*.img").OrderBy(file => file.LastWriteTimeUtc).ToArray();
        _diskIndex = new(StringComparer.OrdinalIgnoreCase); _diskOrder.Clear(); _diskSize = 0;
        foreach (var file in files)
        {
            _diskIndex.Add(file.FullName, (file.Length, _diskOrder.AddLast(file.FullName)));
            _diskSize += file.Length;
        }
        _diskCount = _diskIndex.Count;
        _nextDiskScan = DateTime.UtcNow.AddMinutes(1); Interlocked.Increment(ref _diskScans);
        TrimDisk();
    }
    private void TrimDisk()
    {
        while ((_diskSize > DiskLimit || _diskCount > 512) && _diskOrder.First is { } oldest)
        {
            var entry = _diskIndex![oldest.Value];
            File.Delete(oldest.Value);
            _diskIndex.Remove(oldest.Value); _diskOrder.RemoveFirst();
            _diskSize -= entry.Length; _diskCount--; Interlocked.Increment(ref _diskEvictions);
        }
    }

    private static async Task<BiliImageAsset> DecodeAsync(byte[] raw, bool animate, int maxDimension, CancellationToken token)
    {
        using var timing = ImageResourceCounters.Measure(0);
#if MEMORY_SOAK
        if (App.UseDrawingDecode && !animate && (raw.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 })
            || raw.AsSpan().StartsWith(new byte[] { 255, 216 })))
        {
            using var encoded = new MemoryStream(raw);
            using var image = System.Drawing.Image.FromStream(encoded, false, false);
            if (image.Width is <= 0 or > 4096 || image.Height is <= 0 or > 4096 || (long)image.Width * image.Height > 8_388_608)
                throw new InvalidDataException("图片尺寸超过限制");
            var factor = Math.Min(1d, maxDimension / (double)Math.Max(image.Width, image.Height));
            using var bitmap = new System.Drawing.Bitmap(Math.Max(1, (int)(image.Width * factor)), Math.Max(1, (int)(image.Height * factor)), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(image, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
            }
            token.ThrowIfCancellationRequested();
            return new BiliStaticImageAsset(BiliPixelFrame.FromDrawingBitmap(bitmap), false, maxDimension);
        }
#endif
        using var input = await ToStreamAsync(raw);
        var decoder = await BitmapDecoder.CreateAsync(input);
        if (decoder.PixelWidth is 0 or > 4096 || decoder.PixelHeight is 0 or > 4096 || (long)decoder.PixelWidth * decoder.PixelHeight > 8_388_608)
            throw new InvalidDataException("图片尺寸超过限制");
        var scale = Math.Min(1d, maxDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = Math.Max(1, (int)(decoder.PixelWidth * scale));
        var height = Math.Max(1, (int)(decoder.PixelHeight * scale));
        if (decoder.DecoderInformation.CodecId == BitmapDecoder.GifDecoderId)
            return DecodeGif(raw, width, height, animate, maxDimension, token);
        // Very long animations fall back to their first frame instead of accumulating unbounded frames.
        var count = animate && decoder.FrameCount <= 40 ? (int)decoder.FrameCount : 1;
        var frames = new byte[count][];
        var delays = new int[count];
        BiliPixelFrame? first = null;
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var frame = await decoder.GetFrameAsync((uint)i);
            var pixels = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height, InterpolationMode = BitmapInterpolationMode.Linear },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var data = pixels.DetachPixelData();
            if (i == 0) first = new(width, height, data);
            if (!animate) return new BiliStaticImageAsset(first!, decoder.FrameCount is > 1 and <= 40, maxDimension);
            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, data);
            await encoder.FlushAsync();
            encoded.Seek(0);
            using var reader = new DataReader(encoded);
            await reader.LoadAsync((uint)encoded.Size);
            frames[i] = new byte[checked((int)encoded.Size)]; reader.ReadBytes(frames[i]);
            delays[i] = 100;
            if (count > 1)
            {
                try
                {
                    var properties = await frame.BitmapProperties.GetPropertiesAsync(new[] { "/grctlext/Delay" });
                    if (properties.TryGetValue("/grctlext/Delay", out var delay)) delays[i] = Math.Clamp(Convert.ToInt32(delay.Value) * 10, 100, 2000);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
        }
        return new BiliAnimationAsset(frames, delays, width, height, decoder.FrameCount is > 1 and <= 40, maxDimension, first!);
    }

    private static BiliImageAsset DecodeGif(byte[] raw, int width, int height, bool animate, int limit, CancellationToken token)
    {
        using var stream = new MemoryStream(raw);
        using var gif = System.Drawing.Image.FromStream(stream);
        var dimension = System.Drawing.Imaging.FrameDimension.Time;
        var available = gif.GetFrameCount(dimension);
        var count = animate && available <= 40 ? available : 1;
        var delays = new int[count]; Array.Fill(delays, 100);
        byte[]? timing = null;
        try { timing = gif.GetPropertyItem(0x5100)?.Value; } catch (ArgumentException) { }
        var frames = new byte[count][];
        BiliPixelFrame? first = null;
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            // GDI+ composes GIF offsets and disposal before scaling the complete canvas.
            gif.SelectActiveFrame(dimension, i);
            using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(gif, new System.Drawing.Rectangle(0, 0, width, height));
            }
            if (i == 0) first = BiliPixelFrame.FromDrawingBitmap(bitmap);
            if (!animate) return new BiliStaticImageAsset(first!, available is > 1 and <= 40, limit);
            using var encoded = new MemoryStream(); bitmap.Save(encoded, System.Drawing.Imaging.ImageFormat.Png); frames[i] = encoded.ToArray();
            if (timing is not null && timing.Length >= (i + 1) * 4) delays[i] = (int)Math.Clamp((long)BitConverter.ToInt32(timing, i * 4) * 10, 100, 2000);
        }
        return new BiliAnimationAsset(frames, delays, width, height, available is > 1 and <= 40, limit, first!);
    }

    internal static async Task<InMemoryRandomAccessStream> ToStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        try
        {
            using var writer = new DataWriter(stream);
            writer.WriteBytes(bytes); await writer.StoreAsync(); await writer.FlushAsync();
            writer.DetachStream();
            stream.Seek(0); return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    public void Dispose()
    {
        Sources.Dispose();
        Avatars.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _memory.Clear(); _bytes = 0;
            _capacityChanged.TrySetResult(true);
        }
        _shutdown.Cancel(); _http.Dispose();
        // Semaphore/CTS remain valid for pending shared requests completing their finally blocks.
    }
}

// Error-only, bounded local diagnostics. No viewer names, messages, cookies,
// query strings, sampling timer, or healthy-image logging.
internal static class BiliImageDiagnostics
{
    private const long FileLimit = 32 * 1024;
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BilibiliLiveAssistant");
    internal static void Record(string url, string stage, Exception error, byte[]? data = null)
    {
        try
        {
            var uri = BiliImageCache.NormalizeUrl(url);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri?.AbsoluteUri ?? url)));
            var status = error is HttpRequestException { StatusCode: { } code } ? $" HTTP={(int)code}" : "";
            var signature = data is null ? "" : $" bytes={data.Length} header={Convert.ToHexString(data.AsSpan(0, Math.Min(12, data.Length)))}";
            var line = $"{DateTimeOffset.UtcNow:O} stage={stage} key={hash} host={uri?.Host ?? "invalid"} type={error.GetType().Name} HRESULT=0x{error.HResult:X8}{status}{signature}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "image-errors.log");
                var file = new FileInfo(path);
                if (file.Exists && file.Length + Encoding.UTF8.GetByteCount(line) + 3 > FileLimit) File.WriteAllText(path, "", Encoding.UTF8);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
