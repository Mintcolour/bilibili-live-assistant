namespace BilibiliLiveAssistant.Core;

public enum CredentialSource { None, Manual, Browser }
public enum AccountState { SignedOut, Pending, LoggedIn, Unavailable, Expired }
public enum AccountLossPolicy { Anonymous, StopMonitoring }
public sealed record BrowserSyncSettings
{
    public AccountLossPolicy OnLogout { get; init; }
}
public sealed record BrowserBinding(string SourceId, string Origin, string Browser, string Name, string Token,
    string Generation, string PairRequestId, DateTimeOffset PairExpiresAt, bool Follow = true)
{
    public override string ToString() => $"{Browser} · {Name}";
}
public sealed record BrowserAccount(long Uid, string Name, string Avatar);
// All fields in this aggregate are stored only in the encrypted current-user vault.
public sealed record BrowserAuthState
{
    public long Version { get; init; }
    public CredentialSource Source { get; init; }
    public BrowserBinding? Binding { get; init; }
    public CookieOptions Cookies { get; init; } = new();
    public Dictionary<string, double?> Expirations { get; init; } = new();
    public long SourceRevision { get; init; }
    public string LastRequestId { get; init; } = "";
    public DateTimeOffset? CapturedAt { get; init; }
    public AccountState AccountState { get; init; }
    public BrowserAccount? Account { get; init; }
    public override string ToString() => $"账号状态 {AccountState} · 版本 {Version}";
}
public sealed record SyncMessage
{
    public int ProtocolVersion { get; init; } = 1;
    public string Type { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string Browser { get; init; } = "";
    public string Origin { get; init; } = "";
    public string Name { get; init; } = "";
    public string Token { get; init; } = "";
    public string BindingGeneration { get; init; } = "";
    public long SourceRevision { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public CookieOptions? Cookies { get; init; }
    public Dictionary<string, double?> Expirations { get; init; } = new();
    public string Reason { get; init; } = "";
    public bool Follow { get; init; }
    public long Version { get; init; }
    public override string ToString() => $"{Type} · 协议 {ProtocolVersion}";
}
public sealed record SyncReply(bool Ok, string Code, string Message, long Version = 0,
    string Token = "", string BindingGeneration = "", long SourceRevision = 0, bool Notified = false,
    string AccountState = "", bool Follow = false);
public static class BrowserSyncProtocol
{
    public const int Version = 1;
    public const int MaxBody = 64 * 1024;
    public const string Host = "cn.bililiveassistant.browser_sync";
    public const string InstallationOwner = "BilibiliLiveAssistant.BrowserSync.v1";
    // Fixed development ID; generated from the public key in extension/manifest.json.
    public const string ExtensionId = "lnmblcjfmiaeihkogpmkbjmpngnfkjme";
}
