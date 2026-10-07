using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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
            if (packetLength < headerLength || offset + packetLength > data.Length)
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

    public BiliWbiSigner(HttpClient http) => _http = http;

    public async Task<Dictionary<string, string>> SignAsync(Dictionary<string, string> values, CancellationToken token)
    {
        if (string.IsNullOrEmpty(_key) || DateTimeOffset.UtcNow - _refreshed > TimeSpan.FromHours(11))
            await RefreshAsync(token);
        if (string.IsNullOrEmpty(_key)) return values;

        var sorted = new SortedDictionary<string, string>(values) { ["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() };
        var query = string.Join("&", sorted.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(Sanitize(p.Value))}"));
        sorted["w_rid"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + _key))).ToLowerInvariant();
        return sorted.ToDictionary(p => p.Key, p => p.Value);
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync("https://api.bilibili.com/x/web-interface/nav", token);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
            var image = doc.RootElement.GetProperty("data").GetProperty("wbi_img");
            var img = image.GetProperty("img_url").GetString() ?? string.Empty;
            var sub = image.GetProperty("sub_url").GetString() ?? string.Empty;
            var imgKey = Path.GetFileNameWithoutExtension(new Uri(img).AbsolutePath);
            var subKey = Path.GetFileNameWithoutExtension(new Uri(sub).AbsolutePath);
            var source = imgKey + subKey;
            _key = string.Concat(Mix.Where(i => i < source.Length).Select(i => source[i]));
            _refreshed = DateTimeOffset.UtcNow;
        }
        catch (Exception) { _key = string.Empty; }
    }

    private static string Sanitize(string value) => new(value.Where(c => c is not ('!' or '\'' or '(' or ')' or '*')).ToArray());
}

[ProtoContract]
internal sealed class InteractWordV2
{
    [ProtoMember(1)] public long Uid { get; set; }
    [ProtoMember(2)] public string Uname { get; set; } = string.Empty;
    [ProtoMember(5)] public int MsgType { get; set; }
    [ProtoMember(7)] public long Timestamp { get; set; }
}

public sealed class BiliLiveClient : ILiveRoomClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly BiliWbiSigner _signer;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _runCts;
    private Task? _receiveTask;
    private CookieOptions _cookies = new();
    private int _roomId;
    private string _token = string.Empty;
    private string _host = string.Empty;

    public BiliLiveClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BilibiliLiveAssistant", "1.0"));
        _signer = new BiliWbiSigner(_http);
    }

    public event EventHandler<LiveEvent>? EventReceived;
    public event EventHandler<string>? StatusChanged;
    public bool IsRunning => _runCts is not null;

    public async Task StartAsync(int roomId, CookieOptions cookies, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        _roomId = roomId;
        _cookies = cookies;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        StatusChanged?.Invoke(this, $"正在连接直播间 {roomId}…");
        try
        {
            await ResolveRoomAsync(_runCts.Token);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_runCts.Token), CancellationToken.None);
        }
        catch
        {
            _runCts.Dispose();
            _runCts = null;
            StatusChanged?.Invoke(this, "连接失败：无法获取弹幕服务器");
            throw;
        }
    }

    public async Task StopAsync()
    {
        var cts = Interlocked.Exchange(ref _runCts, null);
        if (cts is null) return;
        cts.Cancel();
        try { if (_socket is { State: WebSocketState.Open }) await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None); } catch { }
        if (_receiveTask is not null) { try { await _receiveTask; } catch { } }
        _socket?.Dispose();
        _socket = null;
        StatusChanged?.Invoke(this, "已断开连接");
        cts.Dispose();
    }

    private async Task ResolveRoomAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo");
        request.Headers.Add("Cookie", CookieHeader());
        var parameters = await _signer.SignAsync(new Dictionary<string, string> { ["id"] = _roomId.ToString(), ["type"] = "0" }, token);
        request.RequestUri = new Uri(request.RequestUri + "?" + string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")));
        using var response = await _http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var data = doc.RootElement.GetProperty("data");
        _token = data.GetProperty("token").GetString() ?? string.Empty;
        var host = data.GetProperty("host_list")[0];
        _host = host.GetProperty("host").GetString() ?? "broadcastlv.chat.bilibili.com";
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var retry = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(token);
                retry = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"连接异常：{ex.Message}");
                retry++;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, retry))), token);
            }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", "Mozilla/5.0");
        socket.Options.SetRequestHeader("Cookie", CookieHeader());
        _socket = socket;
        await socket.ConnectAsync(new Uri($"wss://{_host}:443/sub"), token);
        await socket.SendAsync(BiliPacket.Create(new { uid = 0, roomid = _roomId, protover = 3, platform = "web", type = 2, buvid = _cookies.Buvid3, key = _token }, BiliOperation.Auth), WebSocketMessageType.Binary, true, token);
        StatusChanged?.Invoke(this, "已连接");
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var receive = ReceiveFramesAsync(socket, token);
        while (await heartbeat.WaitForNextTickAsync(token))
        {
            await socket.SendAsync(BiliPacket.Create(new { }, BiliOperation.Heartbeat), WebSocketMessageType.Binary, true, token);
            if (receive.IsCompleted) await receive;
        }
    }

    private async Task ReceiveFramesAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[1024 * 64];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            HandlePackets(message.ToArray());
            message.SetLength(0);
        }
    }

    private void HandlePackets(byte[] data)
    {
        foreach (var packet in BiliPacket.Read(data))
        {
            if (packet.Operation == (int)BiliOperation.AuthReply)
            {
                if (packet.Body.Length >= 4 && BinaryPrimitives.ReadInt32BigEndian(packet.Body.Span[..4]) != 0)
                    throw new InvalidOperationException("B站认证失败，Cookie 可能已过期");
                continue;
            }
            if (packet.Operation != (int)BiliOperation.SendMessageReply) continue;
            foreach (var body in ExpandBody(packet.Version, packet.Body))
            {
                try { HandleCommand(JsonDocument.Parse(body).RootElement); } catch { }
            }
        }
    }

    private static IEnumerable<byte[]> ExpandBody(short version, ReadOnlyMemory<byte> body)
    {
        if (version == 3)
        {
            using var input = new MemoryStream(body.ToArray());
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            brotli.CopyTo(output);
            foreach (var nested in BiliPacket.Read(output.ToArray()))
                foreach (var item in ExpandBody(nested.Version, nested.Body)) yield return item;
        }
        else if (version == 2)
        {
            using var input = new MemoryStream(body.ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            foreach (var nested in BiliPacket.Read(output.ToArray()))
                foreach (var item in ExpandBody(nested.Version, nested.Body)) yield return item;
        }
        else yield return body.ToArray();
    }

    private void HandleCommand(JsonElement root)
    {
        var command = root.TryGetProperty("cmd", out var cmd) ? cmd.GetString() ?? string.Empty : string.Empty;
        var baseCommand = command.Split(':')[0];
        if (baseCommand == "DANMU_MSG")
        {
            var info = root.GetProperty("info");
            Emit(new LiveEvent(LiveEventType.Danmaku, info[2][1].GetString() ?? "", info[1].GetString() ?? "", "#FFFFFF", info[2][0].GetInt64()));
        }
        else if (baseCommand == "SEND_GIFT")
        {
            var data = root.GetProperty("data");
            Emit(new LiveEvent(LiveEventType.Gift, data.GetProperty("uname").GetString() ?? "", $"赠送 {data.GetProperty("giftName").GetString()}x{data.GetProperty("num").GetInt32()}", "#FF69B4"));
        }
        else if (baseCommand == "SUPER_CHAT_MESSAGE")
        {
            var data = root.GetProperty("data");
            Emit(new LiveEvent(LiveEventType.SuperChat, data.GetProperty("user_info").GetProperty("uname").GetString() ?? "", $"¥{data.GetProperty("price").GetInt32()} {data.GetProperty("message").GetString()}", "#FFD700"));
        }
        else if (baseCommand == "INTERACT_WORD_V2")
        {
            var pb = Convert.FromBase64String(root.GetProperty("data").GetProperty("pb").GetString() ?? "");
            using var stream = new MemoryStream(pb);
            var item = Serializer.Deserialize<InteractWordV2>(stream);
            var action = item.MsgType switch { 1 => "进入了直播间", 2 => "关注了主播", 3 => "分享了直播间", 4 => "特别关注了主播", 5 => "互粉了", 6 => "为主播点赞了", _ => "互动了" };
            Emit(new LiveEvent(LiveEventType.Enter, item.Uname, action, "#4CAF50", item.Uid));
        }
        else if (baseCommand == "USER_TOAST_V2")
        {
            var data = root.GetProperty("data");
            if (data.GetProperty("option").GetProperty("source").GetInt32() == 2) return;
            var name = data.GetProperty("sender_uinfo").GetProperty("base").GetProperty("name").GetString() ?? "";
            var level = data.GetProperty("guard_info").GetProperty("guard_level").GetInt32();
            Emit(new LiveEvent(LiveEventType.Guard, name, $"开通了{new[] { "", "总督", "提督", "舰长" }.ElementAtOrDefault(level) ?? "大航海"}！", "#FF69B4"));
        }
    }

    private void Emit(LiveEvent value) => EventReceived?.Invoke(this, value);
    private string CookieHeader() => $"SESSDATA={_cookies.Sessdata}; buvid3={_cookies.Buvid3}; bili_jct={_cookies.BiliJct}";
    public void Dispose() { _ = StopAsync(); _http.Dispose(); }
}
