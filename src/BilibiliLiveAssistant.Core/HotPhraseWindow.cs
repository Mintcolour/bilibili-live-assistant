using System.Text.RegularExpressions;

namespace BilibiliLiveAssistant.Core;

public sealed record HotPhrase(string Text, int Count);

/// <summary>Reception-time rolling window; stores no messages, users or image resources.</summary>
public sealed class HotPhraseWindow
{
    private sealed class Entry(string text) { public string Text = text; public int Count; }
    private readonly object _sync = new();
    private readonly Queue<(string Key, DateTimeOffset Time)> _arrivals = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    public bool HasEntries { get { lock (_sync) return _arrivals.Count > 0; } }
    public void Add(LiveEvent message, DateTimeOffset received)
    {
        if (message.Type != LiveEventType.Danmaku || message.Extra.GetValueOrDefault("sample") is true
            || string.IsNullOrWhiteSpace(message.Content) || message.Content.Length > 512) return;
        var key = DanmakuCardBoard.Key(message);
        lock (_sync)
        {
            Expire(received);
            // Bound both unique phrases and individual arrival timestamps under bursts.
            while (_arrivals.Count >= 8192 || !_entries.ContainsKey(key) && _entries.Count >= 1024) RemoveFirst();
            if (!_entries.TryGetValue(key, out var entry))
            {
                var text = message.Content.Trim();
                try { text = Spaces.Replace(text, " "); } catch (RegexMatchTimeoutException) { }
                entry = new(text); _entries.Add(key, entry);
            }
            entry.Count++; _arrivals.Enqueue((key, received));
        }
    }
    public HotPhrase[] Snapshot(DateTimeOffset now)
    {
        lock (_sync)
        {
            Expire(now);
            return _entries.Values.Where(e => e.Count >= 3).OrderByDescending(e => e.Count)
                .ThenBy(e => e.Text, StringComparer.Ordinal).Take(5).Select(e => new HotPhrase(e.Text, e.Count)).ToArray();
        }
    }
    private void Expire(DateTimeOffset now)
    { while (_arrivals.TryPeek(out var first) && first.Time <= now.AddSeconds(-60)) RemoveFirst(); }
    private void RemoveFirst()
    {
        var first = _arrivals.Dequeue();
        if (--_entries[first.Key].Count == 0) _entries.Remove(first.Key);
    }
    public void Clear() { lock (_sync) { _arrivals.Clear(); _entries.Clear(); } }
}
