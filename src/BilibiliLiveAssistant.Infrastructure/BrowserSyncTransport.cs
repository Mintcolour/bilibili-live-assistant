using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

[SupportedOSPlatform("windows")]
public static class SyncFrames
{
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > BrowserSyncProtocol.MaxBody) throw new InvalidDataException("同步消息大小无效");
        var body = new byte[(int)length];
        try { await stream.ReadExactlyAsync(body, token); return JsonSerializer.Deserialize<T>(body, BrowserAuthVault.Json) ?? throw new InvalidDataException("同步消息无效"); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(body); }
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, BrowserAuthVault.Json);
        try
        {
            if (body.Length > BrowserSyncProtocol.MaxBody) throw new InvalidDataException("同步消息超限");
            var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
            await stream.WriteAsync(header, token); await stream.WriteAsync(body, token); await stream.FlushAsync(token);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(body); }
    }
}
[SupportedOSPlatform("windows")]
public static class BrowserSyncPipe
{
    public static async Task<SyncReply> SendAsync(SyncMessage message, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(1500);
        using var pipe = new NamedPipeClientStream(".", BrowserAuthVault.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(300, timeout.Token);
        await SyncFrames.WriteAsync(pipe, message, timeout.Token);
        return await SyncFrames.ReadAsync<SyncReply>(pipe, timeout.Token);
    }
    public static async Task ListenAsync(Func<SyncMessage, Task<SyncReply>> receive, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(BrowserAuthVault.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            await pipe.WaitForConnectionAsync(token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(3000);
            try
            {
                var message = await SyncFrames.ReadAsync<SyncMessage>(pipe, deadline.Token);
                if (message.Cookies is not null || message.Token.Length != 0) throw new InvalidDataException("通知不得包含凭据");
                var response = await receive(message).WaitAsync(deadline.Token);
                await SyncFrames.WriteAsync(pipe, response, deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException) { }
        }
    }
}
