namespace BilibiliLiveAssistant.Core;

public sealed record OverlayOmission(DateTimeOffset At, string Name, string Content, string Reason);

// UI-thread owned, data only: no queued images and no per-message timers.
public sealed class TimedOverlayQueue
{
    private sealed class Pending(LiveEvent value, DateTimeOffset at, DateTimeOffset ready)
    {
        public LiveEvent Value = value;
        public DateTimeOffset At { get; } = at;
        public DateTimeOffset Ready { get; } = ready;
    }
    private readonly List<Pending> _gifts = new(64), _guards = new(64), _sc = new(64);
    private readonly Queue<OverlayOmission> _omissions = new(100);
    public long Omitted { get; private set; }
    public int Count => _gifts.Count + _guards.Count + _sc.Count;
    public IReadOnlyCollection<OverlayOmission> Omissions => _omissions;
    public event Action<int>? Dropped;
    public void Add(LiveEvent value, DateTimeOffset now, double mergeSeconds)
    {
        Expire(now);
        var list = value.Type == LiveEventType.Guard ? _guards : value.Type == LiveEventType.SuperChat ? _sc : _gifts;
        if (value.Type == LiveEventType.Gift && mergeSeconds > 0)
        {
            var match = list.Find(p => p.Ready > now && GiftAggregation.CanMerge(p.Value, value));
            if (match is not null) { match.Value = GiftAggregation.Combine(match.Value, value); return; }
        }
        if (list.Count == 64)
        {
            if (value.Type != LiveEventType.Gift) { Omit(value, now, "待展示队列已满"); return; }
            Omit(list[0].Value, now, "礼物拥堵，保留较新提示"); list.RemoveAt(0);
        }
        list.Add(new(value, now, now.AddSeconds(value.Type == LiveEventType.Gift ? mergeSeconds : 0)));
    }
    public LiveEvent? Take(DateTimeOffset now, bool superChat)
    {
        Expire(now);
        foreach (var list in superChat ? new[] { _sc } : new[] { _guards, _gifts })
        {
            var index = list.FindIndex(p => p.Ready <= now); if (index < 0) continue;
            var value = list[index].Value; list.RemoveAt(index); return value;
        }
        return null;
    }
    public void Expire(DateTimeOffset now)
    {
        foreach (var list in new[] { _gifts, _guards, _sc })
            while (list.Count > 0 && now - list[0].At >= TimeSpan.FromSeconds(ReferenceEquals(list, _gifts) ? 60 : 300))
            { Omit(list[0].Value, now, "等待超时"); list.RemoveAt(0); }
    }
    public DateTimeOffset? NextWake(bool hasSlot)
    {
        DateTimeOffset? due = null;
        foreach (var list in new[] { _gifts, _guards, _sc }) foreach (var p in list)
        {
            var at = hasSlot ? p.Ready : p.At.AddSeconds(ReferenceEquals(list, _gifts) ? 60 : 300);
            if (due is null || at < due) due = at;
        }
        return due;
    }
    public void Omit(LiveEvent value, DateTimeOffset now, string reason)
    {
        Omitted++; if (_omissions.Count == 100) _omissions.Dequeue();
        _omissions.Enqueue(new(now, value.Username, value.Content.Length > 512 ? value.Content[..512] + "…" : value.Content, reason)); Dropped?.Invoke(1);
    }
    public void Clear() { _gifts.Clear(); _guards.Clear(); _sc.Clear(); _omissions.Clear(); Omitted = 0; }
}
