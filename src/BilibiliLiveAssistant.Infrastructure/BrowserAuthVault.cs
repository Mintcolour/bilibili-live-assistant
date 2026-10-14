using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class BrowserAuthVault
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string UserKey => WindowsIdentity.GetCurrent().User!.Value;
    public static string PipeName => "BilibiliLiveAssistant.Sync." + UserKey;
    public static string MutexName => "Local\\BilibiliLiveAssistant.Auth." + UserKey;
    public string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BilibiliLiveAssistant", "auth");
    public string StatePath => Path.Combine(DirectoryPath, "state.dat");
    public BrowserAuthState Read()
    {
        using var owned = Acquire(); return ReadLocked();
    }
    public BrowserAuthState Update(Func<BrowserAuthState, BrowserAuthState> change)
    {
        using var owned = Acquire(); var previous = ReadLocked(); var next = change(previous);
        if (ReferenceEquals(next, previous) || next == previous) return previous;
        next = next with { Version = checked(previous.Version + 1) };
        SecureDirectory();
        var plain = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temporary = StatePath + ".tmp";
        try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, StatePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return next;
    }
    public BrowserAuthState Revoke() => Update(previous => new() { Version = previous.Version });
    // Only called after a separate, explicit user confirmation. Preserve unreadable data.
    public void RecoverUnreadable()
    {
        using var owned = Acquire();
        try { ReadLocked(); throw new InvalidOperationException("账号存储可正常读取，无需恢复"); }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or JsonException)
        {
            SecureDirectory();
            var backup = Path.Combine(DirectoryPath, "state.unreadable.dat");
            if (File.Exists(backup)) throw new InvalidOperationException("已有损坏状态备份，请先在账号存储目录妥善保留或清理该备份，再恢复。");
            File.Move(StatePath, backup);
        }
    }
    private BrowserAuthState ReadLocked()
    {
        if (!File.Exists(StatePath)) return new();
        if (new FileInfo(StatePath).Length > 128 * 1024) throw new InvalidDataException("本机账号状态超出限制，请重新同步");
        var encrypted = File.ReadAllBytes(StatePath);
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<BrowserAuthState>(plain, Json) ?? throw new InvalidDataException("本机账号状态损坏"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void SecureDirectory()
    {
        var directory = Directory.CreateDirectory(DirectoryPath);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
    private static IDisposable Acquire()
    {
        var mutex = new Mutex(false, MutexName); bool entered;
        try { entered = mutex.WaitOne(500); } catch (AbandonedMutexException) { entered = true; }
        if (!entered) { mutex.Dispose(); throw new TimeoutException("本机状态正忙，请稍后同步"); }
        return new MutexLease(mutex);
    }
    private sealed class MutexLease(Mutex mutex) : IDisposable
    { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }
}
