using System.Text.Json;
using System.Text.Json.Serialization;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
    public string LoadWarning { get; private set; } = "";
    private bool _migrationBlocked;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonSettingsStore(string? directory = null)
    {
        var baseDirectory = directory ?? AppContext.BaseDirectory;
        Path = System.IO.Path.Combine(baseDirectory, "settings.json");
    }

    public string Path { get; }

    public AppSettings Load()
    {
        if (!File.Exists(Path))
            return new AppSettings();

        try
        {
            using var stream = File.OpenRead(Path);
            using var document = JsonDocument.Parse(stream);
            var settings = document.RootElement.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
            if (!OperatingSystem.IsWindows()) return settings;
            try
            {
                var vault = new BrowserAuthVault(); var state = vault.Read();
                var legacy = document.RootElement.TryGetProperty("cookies", out var value) ? value.Deserialize<CookieOptions>(JsonOptions) : null;
                if (legacy is not null && legacy != new CookieOptions())
                {
                    if (state.Version == 0)
                    {
                        vault.Update(previous => previous.Version == 0 ? previous with { Source = CredentialSource.Manual, Cookies = legacy, AccountState = AccountState.Pending } : previous);
                        if (vault.Read().Cookies != legacy) throw new IOException("凭据迁移回读失败");
                    }
                    // No sensitive properties are serialized, even for other settings entry points.
                    var temporary = Path + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
                    File.Move(temporary, Path, true);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _migrationBlocked = true; LoadWarning = "账号迁移未完成，原设置已保留。请修复本机账号存储后重启。"; }
            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (_migrationBlocked) throw new IOException(LoadWarning);
        await _saveGate.WaitAsync(cancellationToken);
        try { await SaveCoreAsync(settings, cancellationToken); }
        finally { _saveGate.Release(); }
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = Path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            // Replace the target only after a complete JSON document is on disk.
            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
