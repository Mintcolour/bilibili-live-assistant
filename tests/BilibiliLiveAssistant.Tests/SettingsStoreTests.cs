using System.Text.Json;
using BilibiliLiveAssistant.Core;
using BilibiliLiveAssistant.Infrastructure;

namespace BilibiliLiveAssistant.Tests;

public class SettingsStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistsCompactEntryListPreference(bool showEntryInList)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bili-live-assistant-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSettingsStore(directory);
            await store.SaveAsync(new AppSettings { Compact = new() { ShowEntryInList = showEntryInList } });
            Assert.Equal(showEntryInList, store.Load().Compact.Normalize().ShowEntryInList);

            // A configuration written before this option existed uses the quieter default.
            await File.WriteAllTextAsync(store.Path, "{\"compact\":{\"showEntry\":true}}");
            var legacy = store.Load();
            Assert.False(legacy.Compact.ShowEntryInList);
            Assert.True(legacy.Compact.ShowEntry);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SavesAndLoadsVersionedSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bili-live-assistant-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSettingsStore(directory);
            var settings = new AppSettings
            {
                RoomId = 12345,
                Cookies = new CookieOptions("buvid", "sess", "jct"),
                Display = new DisplaySettings { FontSize = 32, ShowGift = false },
            };
            await store.SaveAsync(settings);
            var loaded = store.Load();
            Assert.Equal(12345, loaded.RoomId);
            Assert.Equal("sess", loaded.Cookies.Sessdata);
            Assert.Equal(32, loaded.Display.FontSize);
            Assert.False(loaded.Display.ShowGift);
            Assert.Equal(1, JsonDocument.Parse(await File.ReadAllTextAsync(store.Path)).RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
