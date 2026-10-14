using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class BrowserSyncHost(BrowserAuthVault vault)
{
    public static bool ValidOrigin(string origin) => origin.TrimEnd('/') == $"chrome-extension://{BrowserSyncProtocol.ExtensionId}";
    public static bool ValidCookies(CookieOptions cookies) => new[] { cookies.Sessdata, cookies.Buvid3, cookies.BiliJct }
        .All(value => value is not null && Encoding.UTF8.GetByteCount(value) <= 8192 && !value.Any(c => c < 32 || c == 127 || c == ';'));
    public SyncReply Process(SyncMessage message, string origin)
    {
        if (!ValidOrigin(origin)) return Error("origin", "扩展来源不匹配，请安装对应版本");
        if (message.ProtocolVersion != BrowserSyncProtocol.Version) return Error("upgrade", "请更新扩展和本机连接组件");
        if (!Guid.TryParse(message.SourceId, out _) || !Guid.TryParse(message.RequestId, out _)) return Error("invalid", "同步标识无效");
        if (message.Type == "pair.begin")
        {
            if (message.Cookies is not null || message.Token.Length != 0 || message.Name.Length is < 1 or > 60 || message.Browser is not ("Chrome" or "Edge")) return Error("invalid", "来源信息无效");
            return Notify(message with { Origin = origin, Name = message.Name.Trim() }, pairing: true);
        }
        var current = vault.Read(); var binding = current.Binding;
        if (message.Type == "pair.complete")
        {
            if (binding is null || binding.SourceId != message.SourceId || binding.Origin != origin || binding.PairRequestId != message.RequestId || binding.PairExpiresAt < DateTimeOffset.UtcNow)
                return Error("not_approved", "请在助手确认来源后完成连接");
            return new(true, "paired", "来源已绑定，可以同步", current.Version, binding.Token, binding.Generation, current.SourceRevision, Follow: binding.Follow);
        }
        if (binding is null || binding.SourceId != message.SourceId || binding.Origin != origin || binding.Generation != message.BindingGeneration
            || message.Token.Length != 64 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(message.Token), Encoding.UTF8.GetBytes(binding.Token)))
            return Error("unpaired", "绑定已失效，请重新连接直播助手");
        if (message.Type == "sync.status") return new(true, "status", "本机状态已读取", current.Version, SourceRevision: current.SourceRevision,
            AccountState: current.AccountState.ToString(), Follow: binding.Follow);
        if (message.Type == "sync.disconnect")
        {
            current = vault.Update(state => new BrowserAuthState { Version = Authorize(state, message, origin).Version });
            return NotifySaved(current, "disconnected", "已断开本机同步");
        }
        if (message.Type == "sync.configure")
        {
            current = vault.Update(state => Authorize(state, message, origin) with { Binding = state.Binding! with { Follow = message.Follow } });
            return NotifySaved(current, "configured", "自动跟随设置已保存");
        }
        if (message.Type != "session.snapshot" || message.Cookies is not { } cookies || !ValidCookies(cookies)
            || message.SourceRevision <= 0 || message.Expirations.Count > 3 || message.Expirations.Keys.Any(key => key is not ("SESSDATA" or "buvid3" or "bili_jct")))
            return Error("invalid", "登录快照无效");
        var duplicate = false;
        current = vault.Update(state =>
        {
            Authorize(state, message, origin);
            if (message.SourceRevision == state.SourceRevision && message.RequestId == state.LastRequestId) { duplicate = true; return state; }
            if (message.SourceRevision <= state.SourceRevision) throw new InvalidDataException("过期快照已拒绝");
            return state with { Cookies = cookies, Source = CredentialSource.Browser, SourceRevision = message.SourceRevision,
                CapturedAt = message.CapturedAt, LastRequestId = message.RequestId, Expirations = message.Expirations,
                Account = state.Cookies == cookies ? state.Account : null,
                AccountState = cookies.Sessdata.Length == 0 ? AccountState.SignedOut : state.Cookies == cookies ? state.AccountState : AccountState.Pending };
        });
        return NotifySaved(current, duplicate ? "unchanged" : "saved", duplicate ? "状态未变化" : "已保存本机快照，等待助手验证");
    }
    private static BrowserAuthState Authorize(BrowserAuthState state, SyncMessage message, string origin)
    {
        if (state.Binding is not { } binding || binding.SourceId != message.SourceId || binding.Generation != message.BindingGeneration || binding.Origin != origin
            || binding.Token != message.Token) throw new InvalidDataException("绑定已失效");
        return state;
    }
    private static SyncReply NotifySaved(BrowserAuthState state, string code, string text)
    {
        var notified = Notify(new() { Type = "state.changed", Version = state.Version }, false);
        return new(true, code, notified.Ok ? text : text + "；打开助手后刷新本机状态", state.Version,
            SourceRevision: state.SourceRevision, Notified: notified.Ok, AccountState: state.AccountState.ToString(), Follow: state.Binding?.Follow ?? false);
    }
    private static SyncReply Notify(SyncMessage message, bool pairing)
    {
        try { return BrowserSyncPipe.SendAsync(message, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        { return Error(pairing ? "assistant_closed" : "notify_failed", pairing ? "请先在助手打开连接浏览器流程" : "本机通知未送达"); }
    }
    private static SyncReply Error(string code, string text) => new(false, code, text);
}
