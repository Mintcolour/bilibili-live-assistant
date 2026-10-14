using System.Security.Cryptography;
using BilibiliLiveAssistant.Core;
using BilibiliLiveAssistant.Infrastructure;
using Microsoft.UI.Dispatching;

namespace BilibiliLiveAssistant.App;

internal sealed record PendingBrowser(SyncMessage Message, DateTimeOffset ExpiresAt);
/// <summary>UI-thread state coordinator. One reload/verification; no idle polling.</summary>
internal sealed class BrowserAccountController : IAsyncDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly DispatcherQueue _dispatcher;
    private readonly BrowserAuthVault _vault = new();
    private readonly BiliAccountService _identity = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _verify;
    private Task? _listener, _reload;
    private bool _reloadAgain, _force, _disposed;
    private DateTimeOffset _pairingUntil;
    private readonly DispatcherQueueTimer _pairingTimer;
    private CookieOptions? _verifiedCookies;
    private string? _verifiedGeneration;
    private BrowserAccount? _verifiedAccount;
    public BrowserAuthState State { get; private set; } = new();
    public string Error { get; private set; } = "";
    public List<PendingBrowser> Pending { get; } = new(4);
    public event Action? Changed;
    public BrowserAccountController(LiveAssistantViewModel vm, DispatcherQueue dispatcher)
    {
        _vm = vm; _dispatcher = dispatcher; _pairingTimer = dispatcher.CreateTimer();
        _pairingTimer.IsRepeating = false; _pairingTimer.Interval = TimeSpan.FromMinutes(2);
        _pairingTimer.Tick += (_, _) => ClosePairing();
    }
    public Task StartAsync()
    {
        // Creating the server precedes reading the vault, closing the startup notification gap.
        _listener = ListenAsync();
        return RefreshAsync();
    }
    private async Task ListenAsync()
    {
        try { await BrowserSyncPipe.ListenAsync(ReceiveAsync, _shutdown.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _dispatcher.TryEnqueue(() => { if (!_disposed) { Error = "本机通知不可用，请关闭其他助手实例后重启；可刷新本机状态。"; Changed?.Invoke(); } }); }
    }
    private Task<SyncReply> ReceiveAsync(SyncMessage message)
    {
        var completion = new TaskCompletionSource<SyncReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            if (_disposed || message.ProtocolVersion != BrowserSyncProtocol.Version) { completion.TrySetResult(new(false, "closed", "助手不可用")); return; }
            if (message.Type == "state.changed")
            {
                if (message.Version > State.Version) _verify?.Cancel();
                _ = RefreshAsync(); completion.TrySetResult(new(true, "notified", "已接收本机通知", message.Version)); return;
            }
            if (message.Type == "pair.begin" && _pairingUntil > DateTimeOffset.UtcNow && State.Binding is null
                && BrowserSyncHost.ValidOrigin(message.Origin) && Guid.TryParse(message.SourceId, out _) && Guid.TryParse(message.RequestId, out _)
                && message.Browser is "Chrome" or "Edge" && message.Name.Length is > 0 and <= 60)
            {
                Pending.RemoveAll(item => item.ExpiresAt <= DateTimeOffset.UtcNow);
                var existing = Pending.FindIndex(item => item.Message.SourceId == message.SourceId);
                if (existing >= 0) Pending.RemoveAt(existing);
                if (Pending.Count >= 4) { completion.TrySetResult(new(false, "busy", "待确认来源已满，请稍后重试")); return; }
                Pending.Add(new(message, DateTimeOffset.UtcNow.AddMinutes(2))); Changed?.Invoke();
                completion.TrySetResult(new(true, "pending", "请在助手确认这个浏览器来源，再点击完成连接")); return;
            }
            completion.TrySetResult(new(false, "not_open", "请先在助手打开连接浏览器流程"));
        })) completion.TrySetResult(new(false, "closed", "助手正在退出"));
        return completion.Task;
    }
    public void OpenPairing() { _pairingUntil = DateTimeOffset.UtcNow.AddMinutes(2); _pairingTimer.Stop(); _pairingTimer.Start(); Pending.Clear(); Changed?.Invoke(); }
    public void ClosePairing() { _pairingTimer.Stop(); _pairingUntil = default; Pending.Clear(); Changed?.Invoke(); }
    public async Task RecoverAsync()
    {
        _verify?.Cancel(); ClosePairing(); await Task.Run(() => _vault.RecoverUnreadable());
        _verifiedCookies = null; _verifiedAccount = null; await RefreshAsync();
    }
    public async Task ApproveAsync(PendingBrowser pending)
    {
        if (!Pending.Contains(pending) || pending.ExpiresAt <= DateTimeOffset.UtcNow || _pairingUntil <= DateTimeOffset.UtcNow) throw new InvalidOperationException("请求已过期，请在扩展重新连接");
        var message = pending.Message;
        await Task.Run(() => _vault.Update(state => state.Binding is null ? new BrowserAuthState
        {
            Version = state.Version, Source = CredentialSource.Browser,
            Binding = new(message.SourceId, message.Origin, message.Browser, message.Name,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Guid.NewGuid().ToString(), message.RequestId, pending.ExpiresAt),
            AccountState = AccountState.SignedOut,
        } : throw new InvalidOperationException("请先断开已有来源")));
        ClosePairing(); await RefreshAsync();
    }
    public async Task DisconnectAsync()
    {
        _verify?.Cancel(); ClosePairing();
        await Task.Run(() => _vault.Revoke()); _verifiedCookies = null; _verifiedAccount = null;
        await RefreshAsync();
    }
    public async Task ImportAsync(CookieOptions cookies)
    {
        if (!BrowserSyncHost.ValidCookies(cookies)) throw new InvalidOperationException("登录字段格式或长度无效");
        if (State.Binding is not null) throw new InvalidOperationException("请先断开浏览器同步，再手动导入");
        _verify?.Cancel();
        await Task.Run(() => _vault.Update(state => state.Binding is null ? new BrowserAuthState
        { Version = state.Version, Source = CredentialSource.Manual, Cookies = cookies, CapturedAt = DateTimeOffset.Now, AccountState = AccountState.Pending }
            : throw new InvalidOperationException("浏览器来源已经绑定")));
        await RefreshAsync(true);
    }
    public Task RefreshAsync(bool retry = false)
    {
        if (_disposed) return Task.CompletedTask;
        _reloadAgain = true; _force |= retry;
        if (_reload is null || _reload.IsCompleted) _reload = ReloadAsync();
        return _reload;
    }
    private async Task ReloadAsync()
    {
        while (_reloadAgain && !_disposed)
        {
            _reloadAgain = false; var retry = _force; _force = false;
            try
            {
                var state = await Task.Run(() => _vault.Read(), _shutdown.Token);
                if (_disposed) return;
                Error = ""; State = state;
                var generation = state.Binding?.Generation ?? "manual";
                if (state.Cookies.Sessdata.Length == 0)
                {
                    _verifiedCookies = null; _verifiedAccount = null;
                    _vm.UpdateAccountCredentials(new(), "", state.Binding is not null ? "浏览器未登录" : "未连接账号",
                        state.SourceRevision > 0 && _vm.Settings.BrowserSync.OnLogout == AccountLossPolicy.StopMonitoring);
                    Changed?.Invoke(); continue;
                }
                if (!retry && _verifiedCookies == state.Cookies && _verifiedGeneration == generation && _verifiedAccount is not null)
                { _vm.UpdateAccountCredentials(state.Cookies, _verifiedAccount.Name, "", false); Changed?.Invoke(); continue; }
                if (!retry && state.AccountState is AccountState.Expired or AccountState.Unavailable)
                {
                    _vm.UpdateAccountCredentials(new(), "", state.AccountState == AccountState.Expired ? "登录已失效" : "账号暂不可验证",
                        state.AccountState == AccountState.Expired && _vm.Settings.BrowserSync.OnLogout == AccountLossPolicy.StopMonitoring);
                    Changed?.Invoke(); continue;
                }
                State = state with { AccountState = AccountState.Pending, Account = null }; Changed?.Invoke();
                _vm.UpdateAccountCredentials(new(), "", "正在验证账号", false);
                using var verify = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token); _verify = verify;
                var result = await _identity.VerifyAsync(state.Cookies, verify.Token);
                verify.Token.ThrowIfCancellationRequested();
                var latest = await Task.Run(() => _vault.Update(current => current.Version == state.Version
                    ? current with { AccountState = result.State, Account = result.Account } : current), _shutdown.Token);
                if (latest.SourceRevision != state.SourceRevision || latest.Binding?.Generation != state.Binding?.Generation || latest.Cookies != state.Cookies
                    || latest.AccountState != result.State || latest.Account != result.Account) { _reloadAgain = true; continue; }
                State = latest;
                if (result.State == AccountState.LoggedIn && result.Account is not null)
                {
                    var before = _verifiedAccount; _verifiedCookies = state.Cookies; _verifiedGeneration = generation; _verifiedAccount = result.Account;
                    _vm.UpdateAccountCredentials(state.Cookies, result.Account.Name, "", false);
                    if (before is not null && before.Uid != result.Account.Uid) Error = $"账号已切换：{before.Name} → {result.Account.Name}";
                }
                else
                {
                    _verifiedCookies = null; _verifiedAccount = null;
                    _vm.UpdateAccountCredentials(new(), "", result.State == AccountState.Expired ? "登录已失效，请重新登录 B站" : "账号暂不可验证，请重试",
                        result.State == AccountState.Expired && _vm.Settings.BrowserSync.OnLogout == AccountLossPolicy.StopMonitoring);
                }
                Changed?.Invoke();
            }
            catch (OperationCanceledException) { if (!_disposed && !_shutdown.IsCancellationRequested) _reloadAgain = true; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { Error = "本机账号状态不可读取，已有文件未覆盖。请在高级设置恢复账号存储后重新连接。"; State = new(); _vm.UpdateAccountCredentials(new(), "", "账号存储不可用", false); Changed?.Invoke(); }
            finally { _verify = null; }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _shutdown.Cancel(); _verify?.Cancel(); ClosePairing();
        if (_listener is not null) await _listener;
        if (_reload is not null) await _reload;
        _identity.Dispose(); _shutdown.Dispose();
    }
}
