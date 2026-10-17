using System.Text.RegularExpressions;

namespace BilibiliLiveAssistant.Core;

public sealed record HotPhrase(string Text, int Count, string Key = "", LiveMessageAppearance? Appearance = null);

public sealed record HotPhraseSettings
{
    public int FontSize { get; init; } = 16;
    public int EmoteSize { get; init; } = 28;
    public double Opacity { get; init; } = 1;
    public double BackgroundOpacity { get; init; } = .78;
    public bool AnimationEnabled { get; init; } = true;
    public int AnimationDurationMs { get; init; } = 200;
    public bool Enabled { get; init; } = true;
    public int? X { get; init; }
    public int? Y { get; init; }
    public int Width { get; init; } = 480;
    public int Height { get; init; } = 112;
    public HotPhraseSettings Normalize() => this with
    {
        FontSize = Math.Clamp(FontSize, 12, 40), EmoteSize = Math.Clamp(EmoteSize, 16, 80),
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .1, 1) : 1,
        BackgroundOpacity = double.IsFinite(BackgroundOpacity) ? Math.Clamp(BackgroundOpacity, 0, 1) : .78,
        AnimationDurationMs = Math.Clamp(AnimationDurationMs, 100, 500),
        Width = Math.Clamp(Width, 160, 1920), Height = Math.Clamp(Height, 64, 800),
    };
}

/// <summary>Reception-time rolling window; stores no messages, users or image resources.</summary>
public sealed class HotPhraseWindow
{
    private sealed class Entry(string text, LiveMessageAppearance? appearance)
    { public string Text = text; public LiveMessageAppearance? Appearance = appearance; public int Count; }
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
                // Retain only referenced emote metadata, never users, avatars or message resources.
                var appearance = message.Appearance;
                entry = new(text, appearance is null ? null : new LiveMessageAppearance
                { FullEmote = appearance.FullEmote, Emotes = appearance.Emotes
                    .Where(e => e.Token.Length > 0 && text.Contains(e.Token, StringComparison.Ordinal))
                    .OrderBy(e => text.IndexOf(e.Token, StringComparison.Ordinal)).Take(32).ToArray() });
                _entries.Add(key, entry);
            }
            entry.Count++; _arrivals.Enqueue((key, received));
        }
    }
    public HotPhrase[] Snapshot(DateTimeOffset now)
    {
        lock (_sync)
        {
            Expire(now);
            return _entries.Where(e => e.Value.Count >= 3).OrderByDescending(e => e.Value.Count)
                .ThenBy(e => e.Key, StringComparer.Ordinal).Take(5).Select(e => new HotPhrase(e.Value.Text, e.Value.Count, e.Key, e.Value.Appearance)).ToArray();
        }
    }
    private void Expire(DateTimeOffset now)
    { while (_arrivals.TryPeek(out var first) && first.Time <= now.AddSeconds(-30)) RemoveFirst(); }
    private void RemoveFirst()
    {
        var first = _arrivals.Dequeue();
        if (--_entries[first.Key].Count == 0) _entries.Remove(first.Key);
    }
    public void Clear() { lock (_sync) { _arrivals.Clear(); _entries.Clear(); } }
}
