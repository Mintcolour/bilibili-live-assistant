using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using BilibiliLiveAssistant.Core;
using ProtoBuf;

namespace BilibiliLiveAssistant.Infrastructure;

internal enum BiliOperation { Heartbeat = 2, SendMessageReply = 5, Auth = 7, AuthReply = 8 }

internal static class BiliPacket
{
    public const int HeaderSize = 16;

    public static byte[] Create(object payload, BiliOperation operation)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var packet = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), packet.Length);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4, 2), HeaderSize);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), (int)operation);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), 1);
        body.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    public static IEnumerable<(short Version, int Operation, ReadOnlyMemory<byte> Body)> Read(ReadOnlyMemory<byte> data)
    {
        var offset = 0;
        while (offset + HeaderSize <= data.Length)
        {
            var span = data.Span[offset..];
            var packetLength = BinaryPrimitives.ReadInt32BigEndian(span[..4]);
            var headerLength = BinaryPrimitives.ReadInt16BigEndian(span.Slice(4, 2));
            var version = BinaryPrimitives.ReadInt16BigEndian(span.Slice(6, 2));
            var operation = BinaryPrimitives.ReadInt32BigEndian(span.Slice(8, 4));
            if (headerLength < HeaderSize || packetLength < headerLength || packetLength > data.Length - offset)
                yield break;
            yield return (version, operation, data.Slice(offset + headerLength, packetLength - headerLength));
            offset += packetLength;
        }
    }
}

internal sealed class BiliWbiSigner
{
    private static readonly int[] Mix = [46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13];
    private readonly HttpClient _http;
    private string _key = string.Empty;
    private DateTimeOffset _refreshed;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    public long UserId { get; private set; }

    public BiliWbiSigner(HttpClient http) => _http = http;

    public void Reset() { _key = string.Empty; UserId = 0; }

    public async Task<Dictionary<string, string>> SignAsync(Dictionary<string, string> values, CancellationToken token, CookieOptions? cookies = null)
    {
        if (string.IsNullOrEmpty(_key) || DateTimeOffset.UtcNow - _refreshed > TimeSpan.FromHours(11))
        {
            await _refreshGate.WaitAsync(token);
            try
            {
                if (string.IsNullOrEmpty(_key) || DateTimeOffset.UtcNow - _refreshed > TimeSpan.FromHours(11))
                    await RefreshAsync(token, cookies ?? new CookieOptions());
            }
            finally { _refreshGate.Release(); }
        }
        if (string.IsNullOrEmpty(_key)) return values;

        var sorted = new SortedDictionary<string, string>(values) { ["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() };
        var query = string.Join("&", sorted.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(Sanitize(p.Value))}"));
        sorted["w_rid"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + _key))).ToLowerInvariant();
        return sorted.ToDictionary(p => p.Key, p => p.Value);
    }

    private async Task RefreshAsync(CancellationToken token, CookieOptions cookies)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
            BiliHttp.SetHeaders(request, cookies);
            using var response = await _http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
            var data = doc.RootElement.GetProperty("data");
            UserId = !string.IsNullOrWhiteSpace(cookies.Sessdata) && data.TryGetProperty("isLogin", out var login) && login.ValueKind == JsonValueKind.True && data.TryGetProperty("mid", out var mid)
                ? mid.GetInt64() : 0;
            var image = data.GetProperty("wbi_img");
            var img = image.GetProperty("img_url").GetString() ?? string.Empty;
            var sub = image.GetProperty("sub_url").GetString() ?? string.Empty;
            var imgKey = Path.GetFileNameWithoutExtension(new Uri(img).AbsolutePath);
            var subKey = Path.GetFileNameWithoutExtension(new Uri(sub).AbsolutePath);
            var source = imgKey + subKey;
            _key = string.Concat(Mix.Where(i => i < source.Length).Select(i => source[i]));
            _refreshed = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { _key = string.Empty; UserId = 0; }
    }

    private static string Sanitize(string value) => new(value.Where(c => c is not ('!' or '\'' or '(' or ')' or '*')).ToArray());
}

[ProtoContract]
internal sealed class InteractWordV2
{
    [ProtoMember(1)] public long Uid { get; set; }
    [ProtoMember(2)] public string Uname { get; set; } = string.Empty;
    [ProtoMember(3)] public string UsernameColor { get; set; } = string.Empty;
    [ProtoMember(5)] public int MsgType { get; set; }
    [ProtoMember(7)] public long Timestamp { get; set; }
    [ProtoMember(9)] public InteractionMedal? Medal { get; set; }
    [ProtoMember(13)] public string SpreadDescription { get; set; } = string.Empty;
    [ProtoMember(16)] public int GuardLevel { get; set; }
    [ProtoMember(18)] public string TailText { get; set; } = string.Empty;
    [ProtoMember(22)] public InteractionUserInfo? User { get; set; }
    [ProtoMember(23)] public InteractionRelation? Relation { get; set; }
}

// Field numbers verified against B站's web InteractWord / user_dagw schema.
// Only display fields are decoded; unknown fields remain forward compatible.
[ProtoContract]
internal sealed class InteractionMedal
{
    [ProtoMember(2)] public int Level { get; set; }
    [ProtoMember(3)] public string Name { get; set; } = string.Empty;
    [ProtoMember(4)] public int Color { get; set; }
    [ProtoMember(9)] public int GuardLevel { get; set; }
}

[ProtoContract]
internal sealed class InteractionUserInfo
{
    [ProtoMember(2)] public InteractionUserBase? Base { get; set; }
    [ProtoMember(3)] public InteractionUserMedal? Medal { get; set; }
    [ProtoMember(4)] public InteractionWealth? Wealth { get; set; }
    [ProtoMember(6)] public InteractionGuard? Guard { get; set; }
    [ProtoMember(7)] public InteractionAvatarFrame? Frame { get; set; }
}

[ProtoContract]
internal sealed class InteractionUserBase
{
    [ProtoMember(2)] public string Face { get; set; } = string.Empty;
    [ProtoMember(3)] public int NameColor { get; set; }
    [ProtoMember(8)] public string NameColorText { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class InteractionUserMedal
{
    [ProtoMember(1)] public string Name { get; set; } = string.Empty;
    [ProtoMember(2)] public int Level { get; set; }
    [ProtoMember(6)] public int Color { get; set; }
    [ProtoMember(11)] public int GuardLevel { get; set; }
}

[ProtoContract]
internal sealed class InteractionGuard
{
    [ProtoMember(1)] public int Level { get; set; }
}

[ProtoContract]
internal sealed class InteractionWealth
{
    [ProtoMember(1)] public int Level { get; set; }
}

[ProtoContract]
internal sealed class InteractionAvatarFrame
{
    [ProtoMember(2)] public string Url { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class InteractionRelation
{
    [ProtoMember(1)] public string IconUrl { get; set; } = string.Empty;
    [ProtoMember(2)] public string Hint { get; set; } = string.Empty;
}

public sealed class BiliLiveClient : ILiveRoomClient, IDisposable
{
    private const int MaxWebSocketMessageSize = 4 * 1024 * 1024;
    private const int MaxExpandedMessageSize = 16 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly BiliWbiSigner _signer;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Action<LiveEvent> _emit;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _runCts;
    private Task? _receiveTask;
    private TaskCompletionSource? _authReply;
    private CookieOptions _cookies = new();
    private int _requestedRoomId;
    private int _roomId;
    private string _token = string.Empty;
    private string _visitorBuvid = string.Empty;
    private Uri[] _servers = [];
    private long _lastReceived;
    private bool _wasAuthenticated;
    private bool _disposed;

    public BiliLiveClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? BiliHttp.CreateClient();
        _signer = new BiliWbiSigner(_http);
        _emit = Emit;
    }

    public event EventHandler<LiveEvent>? EventReceived;
    public event EventHandler<string>? StatusChanged;
    public bool IsRunning => _runCts is not null;

    public async Task StartAsync(int roomId, CookieOptions cookies, CancellationToken cancellationToken = default)
    {
        if (roomId <= 0) throw new ArgumentOutOfRangeException(nameof(roomId));
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return;
            _requestedRoomId = _roomId = roomId;
            _cookies = cookies;
            _signer.Reset();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = cts.Token;
            _runCts = cts;
            StatusChanged?.Invoke(this, $"正在连接直播间 {roomId}…");
            // Resolve and receive off the UI thread; capture the token before stop
            // can clear/dispose its source.
            _receiveTask = Task.Run(() => ReceiveLoopAsync(token), CancellationToken.None);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            var cts = Interlocked.Exchange(ref _runCts, null);
            if (cts is null) return;
            cts.Cancel();
            try { _socket?.Abort(); } catch (ObjectDisposedException) { }
            if (_receiveTask is not null) { try { await _receiveTask; } catch { } }
            _receiveTask = null;
            _socket = null;
            cts.Dispose();
            StatusChanged?.Invoke(this, "已断开连接");
        }
        finally { _lifecycle.Release(); }
    }

    private async Task InitializeConnectionAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_cookies.Buvid3))
        {
            if (_visitorBuvid.Length == 0) await InitializeVisitorAsync(token);
            _cookies = _cookies with { Buvid3 = _visitorBuvid };
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.live.bilibili.com/room/v1/Room/get_info?room_id={_requestedRoomId}"))
        {
            BiliHttp.SetHeaders(request, _cookies);
            using var response = await _http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
            _roomId = BiliHttp.ReadData(document.RootElement).GetProperty("room_id").GetInt32();
            if (_roomId <= 0) throw new InvalidDataException("直播间 ID 无效");
        }

        for (var attempt = 0; ; attempt++)
        {
            var parameters = await _signer.SignAsync(new Dictionary<string, string>
                { ["id"] = _roomId.ToString(), ["type"] = "0" }, token, _cookies);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?" +
                string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")));
            BiliHttp.SetHeaders(request, _cookies);
            using var response = await _http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
            JsonElement data;
            try { data = BiliHttp.ReadData(document.RootElement); }
            catch (BiliApiException ex) when (ex.Code == -352 && attempt == 0)
            {
                _signer.Reset();
                continue;
            }
            _token = data.GetProperty("token").GetString() ?? string.Empty;
            _servers = data.GetProperty("host_list").EnumerateArray()
                .Select(host =>
                {
                    var name = host.GetProperty("host").GetString() ?? string.Empty;
                    var port = host.TryGetProperty("wss_port", out var value) ? value.GetInt32() : 443;
                    if (!name.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) || port is < 1 or > 65535)
                        throw new InvalidDataException("弹幕服务器地址无效");
                    return new UriBuilder("wss", name, port, "/sub").Uri;
                }).ToArray();
            if (_servers.Length == 0) throw new InvalidDataException("B站未返回可用弹幕服务器");
            return;
        }
    }

    private async Task InitializeVisitorAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/");
        BiliHttp.SetHeaders(request, _cookies);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            foreach (var cookie in cookies)
            {
                if (!cookie.StartsWith("buvid3=", StringComparison.OrdinalIgnoreCase)) continue;
                var end = cookie.IndexOf(';');
                _visitorBuvid = cookie[7..(end < 0 ? cookie.Length : end)];
                break;
            }
        if (_visitorBuvid.Length == 0)
            throw new InvalidDataException("未能获取 B站访客 buvid，请稍后重试");
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var retry = 0;
        var refresh = true;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (refresh) { await InitializeConnectionAsync(token); refresh = false; }
                await ConnectOnceAsync(retry, token);
                // A clean server close still needs a bounded retry delay.
                throw new IOException("弹幕服务器已关闭连接");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) break;
                if (_wasAuthenticated) { retry = 0; _wasAuthenticated = false; }
                if (ex is BiliAuthenticationException)
                {
                    _signer.Reset();
                    refresh = true;
                }
                retry = Math.Min(retry + 1, 1000);
                if (retry % Math.Max(3, _servers.Length) == 0 || _servers.Length == 0) refresh = true;
                var delaySeconds = Math.Min(30, Math.Pow(2, Math.Min(retry, 5)));
                StatusChanged?.Invoke(this, $"连接异常：{ex.Message} · {delaySeconds:0} 秒后重试");
                try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task ConnectOnceAsync(int retry, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        socket.Options.SetRequestHeader("User-Agent", BiliHttp.UserAgent);
        socket.Options.SetRequestHeader("Origin", "https://live.bilibili.com");
        var cookieHeader = BiliHttp.CookieHeader(_cookies);
        if (cookieHeader.Length > 0) socket.Options.SetRequestHeader("Cookie", cookieHeader);
        _socket = socket;
        _authReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wasAuthenticated = false;
        Task? receive = null;
        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(_servers[retry % _servers.Length], connectTimeout.Token);
            }
            _lastReceived = Stopwatch.GetTimestamp();
            await socket.SendAsync(BiliPacket.Create(new
            {
                uid = string.IsNullOrWhiteSpace(_cookies.Sessdata) ? 0 : _signer.UserId,
                roomid = _roomId, protover = 3, platform = "web", type = 2,
                buvid = _cookies.Buvid3, key = _token,
            }, BiliOperation.Auth), WebSocketMessageType.Binary, true, session.Token);
            receive = ReceiveFramesAsync(socket, session.Token);
            var authenticated = _authReply.Task.WaitAsync(TimeSpan.FromSeconds(10), session.Token);
            if (await Task.WhenAny(receive, authenticated) == receive)
            {
                await receive;
                throw new IOException("弹幕服务器在认证前关闭连接");
            }
            await authenticated;
            _wasAuthenticated = true;
            // Announce connected only after the JSON auth reply has succeeded.
            StatusChanged?.Invoke(this, "已连接");
            await SendHeartbeatAsync(socket, session.Token);
            using var cadence = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (true)
            {
                var tick = cadence.WaitForNextTickAsync(session.Token).AsTask();
                if (await Task.WhenAny(receive, tick) == receive) { await receive; return; }
                await tick;
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceived)) > TimeSpan.FromSeconds(45))
                    throw new TimeoutException("长时间未收到弹幕服务器响应");
                await SendHeartbeatAsync(socket, session.Token);
            }
        }
        finally
        {
            session.Cancel();
            socket.Abort();
            if (receive is not null) { try { await receive; } catch { } }
            _authReply = null;
            Interlocked.CompareExchange(ref _socket, null, socket);
        }
    }

    private static Task SendHeartbeatAsync(ClientWebSocket socket, CancellationToken token) =>
        socket.SendAsync(new ArraySegment<byte>(BiliPacket.Create(new { }, BiliOperation.Heartbeat)),
            WebSocketMessageType.Binary, true, token);

    private async Task ReceiveFramesAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (!token.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            Interlocked.Exchange(ref _lastReceived, Stopwatch.GetTimestamp());
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Binary) continue;
            if (message.Length + result.Count > MaxWebSocketMessageSize)
                throw new InvalidDataException("B站弹幕消息超过允许大小");
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            // Parse directly from the reusable frame storage, without copying
            // the whole message and then each individual uncompressed packet.
            HandlePackets(message.GetBuffer().AsMemory(0, (int)message.Length));
            message.SetLength(0);
        }
    }

    private void HandlePackets(ReadOnlyMemory<byte> data, int depth = 0)
    {
        if (depth > 4) throw new InvalidDataException("弹幕压缩嵌套层数过多");
        foreach (var packet in BiliPacket.Read(data))
        {
            if (packet.Version is 2 or 3)
            {
                // Both frame storage and expanded packets are array-backed;
                // keep the compressed input as a view instead of copying it.
                using var input = MemoryMarshal.TryGetArray(packet.Body, out var segment)
                    ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                    : new MemoryStream(packet.Body.ToArray());
                using Stream decoder = packet.Version == 3
                    ? new BrotliStream(input, CompressionMode.Decompress)
                    : new ZLibStream(input, CompressionMode.Decompress);
                using var expanded = new MemoryStream();
                var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
                try
                {
                    int length;
                    while ((length = decoder.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (expanded.Length + length > MaxExpandedMessageSize)
                            throw new InvalidDataException("解压后的弹幕消息超过允许大小");
                        expanded.Write(chunk, 0, length);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(chunk); }
                HandlePackets(expanded.GetBuffer().AsMemory(0, (int)expanded.Length), depth + 1);
                continue;
            }
            if (packet.Operation == (int)BiliOperation.AuthReply)
            {
                using var document = JsonDocument.Parse(packet.Body);
                if (!document.RootElement.TryGetProperty("code", out var code) || !code.TryGetInt32(out var number))
                    throw new BiliAuthenticationException("弹幕认证响应格式异常");
                if (number != 0)
                    throw new BiliAuthenticationException($"弹幕认证失败（{number}），正在刷新连接凭据");
                _authReply?.TrySetResult();
                continue;
            }
            if (packet.Operation != (int)BiliOperation.SendMessageReply || packet.Body.IsEmpty) continue;
            try
            {
                using var document = JsonDocument.Parse(packet.Body);
                BiliEventParser.Parse(document.RootElement, _emit);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
                or IndexOutOfRangeException or FormatException or IOException or ArgumentException or OverflowException or ProtoException)
            {
                // One incompatible event must not discard the remaining packets
                // in this batch or trigger expensive connection reinitialization.
            }
        }
    }

    private void Emit(LiveEvent value) => EventReceived?.Invoke(this, value);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = StopAsync();
        if (_ownsHttp) _http.Dispose();
    }
}
