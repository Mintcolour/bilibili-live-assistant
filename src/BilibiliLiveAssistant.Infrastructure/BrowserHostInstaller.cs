using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class BrowserHostInstaller
{
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BilibiliLiveAssistant", "browser-sync");
    private static string RegistryPath(string browser) => $"Software\\{(browser == "Chrome" ? "Google\\Chrome" : "Microsoft\\Edge")}\\NativeMessagingHosts\\{BrowserSyncProtocol.Host}";
    private string ManifestPath(string browser) => Path.Combine(Root, browser.ToLowerInvariant() + ".json");
    public bool IsInstalled(string browser)
    {
        try
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
            using var key = user.OpenSubKey(RegistryPath(browser));
            if (key?.GetValue("Owner") as string != BrowserSyncProtocol.InstallationOwner || key.GetValue(null) as string != ManifestPath(browser)) return false;
            using var json = JsonDocument.Parse(File.ReadAllText(ManifestPath(browser)));
            var manifest = json.RootElement;
            var path = manifest.GetProperty("path").GetString()!;
            return key.GetValue("ProtocolVersion") is int protocol && protocol == BrowserSyncProtocol.Version
                && manifest.GetProperty("name").GetString() == BrowserSyncProtocol.Host && Inside(path) && File.Exists(path)
                && File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "protocol-version.txt")).Trim() == BrowserSyncProtocol.Version.ToString()
                && manifest.GetProperty("allowed_origins").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { $"chrome-extension://{BrowserSyncProtocol.ExtensionId}/" });
        }
        catch { return false; }
    }
    public void Install(string browser, string package)
    {
        ValidateBrowser(browser); using var gate = BridgeGate();
        var source = Path.GetFullPath(package);
        if (!File.Exists(Path.Combine(source, "BilibiliLiveAssistant.BrowserHost.exe")) || !File.Exists(Path.Combine(source, "coreclr.dll"))) throw new IOException("发布包缺少本机组件，请重新解压完整版本");
        if (File.ReadAllText(Path.Combine(source, "protocol-version.txt")).Trim() != BrowserSyncProtocol.Version.ToString()) throw new IOException("组件协议版本不匹配，请使用完整发布包");
        CheckOwnedRoot(); CheckRegistry(browser);
        Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, "owner.txt"), BrowserSyncProtocol.InstallationOwner);
        var version = "component-" + Guid.NewGuid().ToString("N");
        var staging = Path.Combine(Root, version + ".staging"); var installed = Path.Combine(Root, version);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in SafeFiles(source))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("组件路径无效");
                var relative = Path.GetRelativePath(source, file); var destination = Path.GetFullPath(Path.Combine(staging, relative));
                if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("组件路径无效");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
                using var original = File.OpenRead(file); using var copied = File.OpenRead(destination);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), SHA256.HashData(copied))) throw new IOException("组件复制校验失败");
            }
            Directory.Move(staging, installed);
            var manifest = new { name = BrowserSyncProtocol.Host, description = "B站直播助手本机登录同步", path = Path.Combine(installed, "BilibiliLiveAssistant.BrowserHost.exe"),
                type = "stdio", allowed_origins = new[] { $"chrome-extension://{BrowserSyncProtocol.ExtensionId}/" } };
            var temporary = ManifestPath(browser) + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(manifest)); File.Move(temporary, ManifestPath(browser), true);
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view); using var key = user.CreateSubKey(RegistryPath(browser));
                key.SetValue("Owner", BrowserSyncProtocol.InstallationOwner); key.SetValue("ProtocolVersion", BrowserSyncProtocol.Version);
                key.SetValue(null, ManifestPath(browser));
            }
            TrimComponents();
        }
        finally
        {
            if (Directory.Exists(staging) && Inside(staging)) { foreach (var _ in SafeFiles(staging)) { } Directory.Delete(staging, true); }
            // Interrupted registration can be repaired; keep at most three unreferenced versions.
            try { TrimComponents(); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (JsonException) { }
        }
    }
    public void Uninstall(bool clearCredentials)
    {
        using var gate = BridgeGate(); CheckOwnedRoot();
        var vault = new BrowserAuthVault();
        if (clearCredentials) vault.Revoke();
        else vault.Update(state => new() { Version = state.Version, Cookies = state.Cookies, Source = state.Cookies.Sessdata.Length > 0 ? CredentialSource.Manual : CredentialSource.None,
            AccountState = state.Cookies.Sessdata.Length > 0 ? AccountState.Pending : AccountState.SignedOut, CapturedAt = state.CapturedAt });
        foreach (var browser in new[] { "Chrome", "Edge" })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view); using var key = user.OpenSubKey(RegistryPath(browser));
                var owned = key?.GetValue("Owner") as string == BrowserSyncProtocol.InstallationOwner && key.GetValue(null) as string == ManifestPath(browser);
                key?.Dispose(); if (owned) user.DeleteSubKeyTree(RegistryPath(browser), false);
            }
        }
        // If another registration uses this root, keep its manifest and component.
        foreach (var browser in new[] { "Chrome", "Edge" })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            { using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view); using var key = user.OpenSubKey(RegistryPath(browser)); if (key is not null) return; }
        if (Directory.Exists(Root)) { foreach (var _ in SafeFiles(Root)) { } Directory.Delete(Path.GetFullPath(Root), true); }
    }
    private void CheckRegistry(string browser)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view); using var key = user.OpenSubKey(RegistryPath(browser));
            if (key is not null && (key.GetValue("Owner") as string != BrowserSyncProtocol.InstallationOwner || key.GetValue(null) as string != ManifestPath(browser)))
                throw new IOException("该连接注册项不属于本项目，未进行修改");
        }
    }
    private void CheckOwnedRoot()
    {
        if (Directory.Exists(Root) && (!File.Exists(Path.Combine(Root, "owner.txt")) || File.ReadAllText(Path.Combine(Root, "owner.txt")) != BrowserSyncProtocol.InstallationOwner))
            throw new IOException("连接组件目录归属不匹配，未进行修改");
        if (Directory.Exists(Root) && (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new IOException("组件目录不能是重定向路径");
    }
    private bool Inside(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<string> SafeFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("组件目录不能是重定向路径");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("组件路径不能是重定向路径");
            if ((attributes & FileAttributes.Directory) == 0) yield return entry;
            else foreach (var file in SafeFiles(entry)) yield return file;
        }
    }
    private void TrimComponents()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var browser in new[] { "Chrome", "Edge" })
            if (File.Exists(ManifestPath(browser)))
            { using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath(browser))); referenced.Add(Path.GetDirectoryName(doc.RootElement.GetProperty("path").GetString()!)!); }
        foreach (var folder in new DirectoryInfo(Root).EnumerateDirectories("component-*").OrderByDescending(info => info.CreationTimeUtc).Skip(3))
            if (Inside(folder.FullName) && !referenced.Contains(folder.FullName) && (folder.Attributes & FileAttributes.ReparsePoint) == 0)
            { foreach (var _ in SafeFiles(folder.FullName)) { } folder.Delete(true); }
    }
    private static void ValidateBrowser(string browser) { if (browser is not ("Chrome" or "Edge")) throw new ArgumentException("请选择 Chrome 或 Edge"); }
    private static IDisposable BridgeGate()
    {
        var mutex = new Mutex(false, "Local\\BilibiliLiveAssistant.Bridge." + BrowserAuthVault.UserKey); bool entered;
        try { entered = mutex.WaitOne(1000); } catch (AbandonedMutexException) { entered = true; }
        if (!entered) { mutex.Dispose(); throw new IOException("连接组件正忙，请稍后重试"); }
        return new GateLease(mutex);
    }
    private sealed class GateLease(Mutex mutex) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }
}
