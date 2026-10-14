using System.Text.Json;
using BilibiliLiveAssistant.Core;
using BilibiliLiveAssistant.Infrastructure;

// stdout is exclusively one Native Messaging response. No credentials in diagnostics.
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
SyncReply reply;
try
{
    using var processing = new Mutex(false, "Local\\BilibiliLiveAssistant.Bridge." + BrowserAuthVault.UserKey);
    var entered = false;
    try
    {
        try { entered = processing.WaitOne(0); } catch (AbandonedMutexException) { entered = true; }
        if (!entered) reply = new(false, "busy", "本机同步正忙，请稍后重试");
        else
        {
            var message = SyncFrames.ReadAsync<SyncMessage>(Console.OpenStandardInput(), deadline.Token).GetAwaiter().GetResult();
            reply = new BrowserSyncHost(new()).Process(message, args.FirstOrDefault() ?? "");
        }
    }
    finally { if (entered) processing.ReleaseMutex(); }
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{ reply = new(false, "state_error", "同步失败，请修复连接组件或重新同步；已有数据未被覆盖"); }
try { SyncFrames.WriteAsync(Console.OpenStandardOutput(), reply, deadline.Token).GetAwaiter().GetResult(); }
catch (Exception ex) when (ex is not OutOfMemoryException) { Environment.ExitCode = 1; }
