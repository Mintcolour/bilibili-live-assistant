using System.Text.Json;
using System.Text.Json.Serialization;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
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
            return JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
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
