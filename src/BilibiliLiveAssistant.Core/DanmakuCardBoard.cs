using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BilibiliLiveAssistant.Core;

public sealed record DanmakuCardSettings
{
    public bool Enabled { get; init; }
    public int Columns { get; init; } = 2;
    public int Height { get; init; } = 180;
    public bool AutoHide { get; init; }
    public int RepeatThreshold { get; init; } = 20;
    public int HideSeconds { get; init; } = 30;
    public DanmakuCardSettings Normalize() => this with { Columns = Math.Clamp(Columns, 1, 4), Height = Math.Clamp(Height, 140, 320),
        RepeatThreshold = Math.Clamp(RepeatThreshold, 2, 1000), HideSeconds = Math.Clamp(HideSeconds, 5, 600) };
}
public sealed class CardSender(LiveEvent value)
{
    public long Uid { get; } = value.Uid;
    public string Name { get; } = value.Username;
    public long Count { get; internal set; } = 1;
    public DateTimeOffset LastSeen { get; internal set; } = value.OccurredAt;
}
public sealed class DanmakuCardEntry(string key, LiveEvent value)
{
    public string Key { get; } = key;
    public DashboardRecord Record { get; } = new(value);
    public long Count { get; private set; }
    public DateTimeOffset LastSeen { get; private set; }
    public long UnknownCount { get; private set; }
    public long UnlistedCount { get; private set; }
    public bool Protected { get; set; }
    public Dictionary<long, CardSender> Senders { get; } = new();
    internal void Add(LiveEvent message)
    {
        Count = Increment(Count); LastSeen = message.OccurredAt;
        if (message.Uid <= 0) UnknownCount = Increment(UnknownCount);
        else if (Senders.TryGetValue(message.Uid, out var sender)) { sender.Count = Increment(sender.Count); sender.LastSeen = message.OccurredAt; }
        else if (Senders.Count < 64) Senders.Add(message.Uid, new(message));
        else UnlistedCount = Increment(UnlistedCount);
    }
    private static long Increment(long value) => value == long.MaxValue ? value : value + 1;
}
/// <summary>Bounded UI-thread-owned slots. Empty slots are never compacted.</summary>
public sealed class DanmakuCardBoard
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly Dictionary<string, DateTimeOffset> _temporary = new();
    private readonly HashSet<string> _manual = new();
    public DanmakuCardEntry?[] Slots { get; } = new DanmakuCardEntry?[32];
    public int Capacity { get; private set; } = 6;
    public long Version { get; private set; }
    public event Action? Changed;
    public int HiddenCount => _manual.Count;
    public void Resize(int capacity)
    {
        capacity = Math.Clamp(capacity, 1, 32);
        if (capacity == Capacity) return;
        for (var i = capacity; i < Slots.Length; i++) Slots[i] = null;
        Capacity = capacity; Notify();
    }
    public void AddBatch(IReadOnlyList<LiveEvent> batch, DanmakuCardSettings settings)
    {
        var changed = false; var now = DateTimeOffset.UtcNow;
        foreach (var expired in _temporary.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) _temporary.Remove(expired);
        foreach (var message in batch)
        {
            if (message.Type != LiveEventType.Danmaku) continue;
            var key = Key(message);
            if (_manual.Contains(key) || _temporary.ContainsKey(key)) continue;
            var index = Array.FindIndex(Slots, 0, Capacity, entry => entry?.Key == key);
            if (index < 0)
            {
                index = Array.FindIndex(Slots, 0, Capacity, entry => entry is null);
                if (index < 0)
                {
                    var oldest = DateTimeOffset.MaxValue;
                    for (var i = 0; i < Capacity; i++)
                        if (Slots[i] is { Protected: false } entry && entry.LastSeen < oldest) { index = i; oldest = entry.LastSeen; }
                }
                if (index < 0) continue;
                Slots[index] = new(key, message);
            }
            var item = Slots[index]!; item.Add(message); changed = true;
            if (settings.AutoHide && item.Count >= settings.RepeatThreshold)
            {
                if (_temporary.Count == 512) _temporary.Remove(_temporary.MinBy(x => x.Value).Key);
                _temporary[key] = now.AddSeconds(settings.HideSeconds); Slots[index] = null;
            }
        }
        if (changed) Notify();
    }
    public bool Hide(DanmakuCardEntry entry)
    {
        if (_manual.Count >= 512 && !_manual.Contains(entry.Key)) return false;
        _manual.Add(entry.Key);
        for (var i = 0; i < Capacity; i++) if (Slots[i]?.Key == entry.Key) Slots[i] = null;
        Notify(); return true;
    }
    public void ClearHidden() { _manual.Clear(); _temporary.Clear(); Notify(); }
    public void Clear() { Array.Clear(Slots); _manual.Clear(); _temporary.Clear(); Notify(); }
    private void Notify() { Version++; Changed?.Invoke(); }
    internal static string Key(LiveEvent message)
    {
        var content = message.Content;
        try { content = Whitespace.Replace(content.Trim(), " "); } catch (RegexMatchTimeoutException) { }
        var signature = new StringBuilder(content);
        if (message.Appearance?.FullEmote is { } full) signature.Append('\0').Append(full.Url);
        if (message.Appearance is { } appearance)
            foreach (var emote in appearance.Emotes.Where(e => e.Token.Length > 0 && content.Contains(e.Token, StringComparison.Ordinal))
                .OrderBy(e => e.Token, StringComparer.Ordinal).ThenBy(e => e.Url, StringComparer.Ordinal))
                signature.Append('\0').Append(emote.Token).Append('\0').Append(emote.Url);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }
}
