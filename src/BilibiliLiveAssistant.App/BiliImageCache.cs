using System.Security.Cryptography;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BilibiliLiveAssistant.App;

internal sealed record BiliImageAsset(byte[][] Frames, int[] Delays, int Width, int Height, bool CanAnimate)
{
    public long Size => Frames.Sum(frame => (long)frame.Length) + (long)Width * Height * 4 * Frames.Length;
}

/// <summary>Shared bounded CDN downloads and decoded PNG frames. No account lookups or cookies.</summary>
internal sealed class BiliImageCache : IDisposable
{
    private const int DownloadLimit = 2 * 1024 * 1024;
    private const long MemoryLimit = 24 * 1024 * 1024;
    private const long DiskLimit = 128 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, (BiliImageAsset Asset, long Used)> _memory = new();
    private readonly Dictionary<string, Task<BiliImageAsset?>> _inflight = new();
    private readonly Dictionary<string, DateTimeOffset> _failures = new();
    private TaskCompletionSource<bool> _capacityChanged = NewCapacitySignal();
    private readonly SemaphoreSlim _downloads = new(2, 2);
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BilibiliLiveAssistant", "image-cache");
    private long _bytes;
    private long _useSequence;
    private int _diskWrites;
    private long _diskSize;
    private int _diskCount;
    private bool _disposed;

    public BiliImageCache()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BilibiliLiveAssistant/1.0");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com/");
    }

    private static TaskCompletionSource<bool> NewCapacitySignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<BiliImageAsset?> GetAsync(string url, bool animate = false)
    {
        if (NormalizeUrl(url) is not { } uri) return Task.FromResult<BiliImageAsset?>(null);
        var key = uri.AbsoluteUri + (animate ? "|animated" : "|static");
        lock (_gate)
            return GetOrStart(key, uri, animate) ?? Task.FromResult<BiliImageAsset?>(null);
    }

    // Visible views wait for admission instead of mistaking a full queue for a
    // failed image. Unloading cancels only that view's wait, never a shared load.
    internal async Task<BiliImageAsset?> GetForViewAsync(string url, bool animate, CancellationToken cancellation)
    {
        if (NormalizeUrl(url) is not { } uri) return null;
        var key = uri.AbsoluteUri + (animate ? "|animated" : "|static");
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            Task<BiliImageAsset?>? pending;
            Task capacityChanged;
            lock (_gate)
            {
                pending = GetOrStart(key, uri, animate);
                capacityChanged = _capacityChanged.Task;
            }
            if (pending is not null) return await pending.WaitAsync(cancellation).ConfigureAwait(false);
            await capacityChanged.WaitAsync(cancellation).ConfigureAwait(false);
        }
    }

    // Caller owns _gate. Null means temporary capacity pressure; an actual
    // failure/disposal returns a completed task so it cannot cause a retry loop.
    private Task<BiliImageAsset?>? GetOrStart(string key, Uri uri, bool animate)
    {
        if (_disposed) return Task.FromResult<BiliImageAsset?>(null);
        if (_memory.TryGetValue(key, out var entry))
        { _memory[key] = (entry.Asset, ++_useSequence); return Task.FromResult<BiliImageAsset?>(entry.Asset); }
        if (_inflight.TryGetValue(key, out var pending)) return pending;
        if (_failures.TryGetValue(key, out var failed) && failed > DateTimeOffset.UtcNow)
            return Task.FromResult<BiliImageAsset?>(null);
        if (_inflight.Count >= 32) return null;
        var task = Task.Run(() => LoadAsync(key, uri, animate, _shutdown.Token));
        _inflight.Add(key, task);
        return task;
    }

    internal byte[]? TryGetFrame(string url)
    {
        if (NormalizeUrl(url) is not { } uri) return null;
        lock (_gate)
        {
            if (_memory.TryGetValue(uri.AbsoluteUri + "|static", out var entry)) return entry.Asset.Frames[0];
            if (_memory.TryGetValue(uri.AbsoluteUri + "|animated", out entry)) return entry.Asset.Frames[0];
            return null;
        }
    }

    internal static Uri? NormalizeUrl(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048) return null;
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var host = uri.DnsSafeHost;
        if (!new[] { "hdslb.com", "biliimg.com", "bilivideo.com" }.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))) return null;
        return new UriBuilder(uri) { Scheme = "https", Port = -1, Fragment = "" }.Uri;
    }

    private async Task<BiliImageAsset?> LoadAsync(string key, Uri uri, bool animate, CancellationToken token)
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
            try { asset = await DecodeAsync(raw, animate, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
            {
                try { File.Delete(DiskPath(uri)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            lock (_gate)
            {
                if (_disposed) return null;
                while (_bytes + asset.Size > MemoryLimit && _memory.Count > 0)
                {
                    var oldest = _memory.MinBy(item => item.Value.Used);
                    _bytes -= oldest.Value.Asset.Size; _memory.Remove(oldest.Key);
                }
                if (asset.Size <= MemoryLimit) { _memory[key] = (asset, ++_useSequence); _bytes += asset.Size; }
            }
            return asset;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException || !token.IsCancellationRequested)
                BiliImageDiagnostics.Record(uri.AbsoluteUri, stage, ex, diagnosticData);
            lock (_gate)
            {
                if (_failures.Count >= 256) _failures.Remove(_failures.MinBy(x => x.Value).Key);
                _failures[key] = DateTimeOffset.UtcNow.AddMinutes(5);
            }
            return null;
        }
        finally
        {
            if (entered) _downloads.Release();
            lock (_gate)
            {
                _inflight.Remove(key);
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
                return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        byte[]? data = null;
        var current = uri;
        for (var redirect = 0; redirect < 4; redirect++)
        {
            using var response = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
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
            var previous = new FileInfo(path);
            var previousLength = previous.Exists ? previous.Length : 0;
            var wasPresent = previous.Exists;
            // Each key is written once under the disk gate; bounded reads reject an interrupted write.
            await File.WriteAllBytesAsync(path, data, token).ConfigureAwait(false);
            _diskSize += data.Length - previousLength;
            if (!wasPresent) _diskCount++;
            if (_diskWrites++ % 16 == 0 || _diskSize > DiskLimit || _diskCount > 512) TrimDisk();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { _diskGate.Release(); }
        return data;
    }

    private string DiskPath(Uri uri) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))) + ".img");

    private void TrimDisk()
    {
        var files = new DirectoryInfo(_directory).EnumerateFiles("*.img").OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        long size = 0;
        var kept = 0;
        for (var i = 0; i < files.Length; i++)
        {
            if (kept >= 512 || size + files[i].Length > DiskLimit) files[i].Delete();
            else { size += files[i].Length; kept++; }
        }
        _diskSize = size; _diskCount = kept;
    }

    private static async Task<BiliImageAsset> DecodeAsync(byte[] raw, bool animate, CancellationToken token)
    {
        using var input = await ToStreamAsync(raw);
        var decoder = await BitmapDecoder.CreateAsync(input);
        if (decoder.PixelWidth is 0 or > 4096 || decoder.PixelHeight is 0 or > 4096 || (long)decoder.PixelWidth * decoder.PixelHeight > 8_388_608)
            throw new InvalidDataException("图片尺寸超过限制");
        var scale = Math.Min(1d, 256d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = Math.Max(1, (int)(decoder.PixelWidth * scale));
        var height = Math.Max(1, (int)(decoder.PixelHeight * scale));
        if (decoder.DecoderInformation.CodecId == BitmapDecoder.GifDecoderId)
            return DecodeGif(raw, width, height, animate, token);
        // Very long animations fall back to their first frame instead of accumulating unbounded frames.
        var count = animate && decoder.FrameCount <= 40 ? (int)decoder.FrameCount : 1;
        var frames = new byte[count][];
        var delays = new int[count];
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var frame = await decoder.GetFrameAsync((uint)i);
            var pixels = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height, InterpolationMode = BitmapInterpolationMode.Linear },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels.DetachPixelData());
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
        return new(frames, delays, width, height, decoder.FrameCount is > 1 and <= 40);
    }

    private static BiliImageAsset DecodeGif(byte[] raw, int width, int height, bool animate, CancellationToken token)
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
            using var encoded = new MemoryStream(); bitmap.Save(encoded, System.Drawing.Imaging.ImageFormat.Png); frames[i] = encoded.ToArray();
            if (timing is not null && timing.Length >= (i + 1) * 4) delays[i] = (int)Math.Clamp((long)BitConverter.ToInt32(timing, i * 4) * 10, 100, 2000);
        }
        return new(frames, delays, width, height, available is > 1 and <= 40);
    }

    internal static async Task<InMemoryRandomAccessStream> ToStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(bytes); await writer.StoreAsync(); await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0); return stream;
    }
    public void Dispose()
    {
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
