using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BilibiliLiveAssistant.Infrastructure;

namespace BilibiliLiveAssistant.Tests;

public sealed class LiveClientShutdownTests
{
    [Fact]
    public async Task StopDoesNotWaitForAnUnresponsiveServerCloseHandshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connect = socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        var headers = new StringBuilder();
        var buffer = new byte[1];
        while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            Assert.True(headers.Length < 8192);
            Assert.Equal(1, await stream.ReadAsync(buffer, timeout.Token));
            headers.Append((char)buffer[0]);
        }
        var key = headers.ToString().Split("\r\n")
            .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), timeout.Token);
        await connect;
        // Keep the peer connected but deliberately never answer a close frame.
        using var client = new BiliLiveClient();
        typeof(BiliLiveClient).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, socket);
        typeof(BiliLiveClient).GetField("_runCts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, new CancellationTokenSource());
        await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(client.IsRunning);
        Assert.NotEqual(WebSocketState.Open, socket.State);
        await client.StopAsync();
    }
}
