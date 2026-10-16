namespace BilibiliLiveAssistant.Core;

// UI-thread owned. Data only: no bitmaps, downloads, per-message timers or unbounded history.
public sealed class CompactMessage(long id, LiveEvent value, DateTimeOffset received)
{
    public long Id { get; } = id;
    public LiveEvent Value { get; internal set; } = value;
    public DateTimeOffset Received { get; } = received;
    public DateTimeOffset? Expires { get; internal set; }
}

public sealed class CompactTimeline
{
    private sealed record Pending(CompactMessage Message, DateTimeOffset Ready);
    private readonly List<CompactMessage> _rows = new(100);
    private readonly List<Pending> _gifts = new(64), _guards = new(64), _sc = new(64), _follows = new(32);
    private long _sequence;
    public IReadOnlyList<CompactMessage> Rows => _rows;
    public CompactMessage? SuperChat { get; private set; }
    public CompactMessage? Gift { get; private set; }
    public CompactMessage? Entry { get; private set; }
    public CompactMessage? Follow { get; private set; }
    public long Omitted { get; private set; }
    public event Action<int>? Dropped;
    public int Queued => _gifts.Count + _guards.Count + _sc.Count + _follows.Count;
    private DateTimeOffset _nextGift, _nextSc, _nextEntry;

    public void AddBatch(IReadOnlyList<LiveEvent> batch, DateTimeOffset now, AppSettings settings)
    {
        Advance(now, settings);
        foreach (var value in batch)
        {
            if (value.Extra.GetValueOrDefault("sample") is true || value.Type is LiveEventType.Online or LiveEventType.Rank) continue;
            var kind = value.Type switch { LiveEventType.Gift or LiveEventType.Guard => OverlayRegionKind.Gift,
                LiveEventType.SuperChat => OverlayRegionKind.SuperChat, LiveEventType.Enter => OverlayRegionKind.Interaction, _ => OverlayRegionKind.Danmaku };
            if (!OverlayRegions.Enabled(settings.Display, kind)) continue;
            var merge = OverlayRegions.Get(settings.Display, OverlayRegionKind.Gift).MergeSeconds;
            if (value.Type == LiveEventType.Gift && merge > 0 && _gifts.Find(p => now < p.Ready && GiftAggregation.CanMerge(p.Message.Value, value)) is { } match)
            { match.Message.Value = GiftAggregation.Combine(match.Message.Value, value); continue; }
            var message = new CompactMessage(++_sequence, value, now);
            if (_rows.Count == 100) _rows.RemoveAt(0);
            _rows.Add(message);
            if (value.Type == LiveEventType.Gift) Add(_gifts, message, now.AddSeconds(merge), 64, true);
            else if (value.Type == LiveEventType.Guard) Add(_guards, message, now, 64, false);
            else if (value.Type == LiveEventType.SuperChat) Add(_sc, message, now, 64, false);
            else if (LiveInteractions.IsFollow(value)) Add(_follows, message, now, 32, false);
            else if (LiveInteractions.IsEntry(value))
            {
                Entry = message; message.Expires = now.AddSeconds(OverlayRegions.Get(settings.Display, OverlayRegionKind.Interaction).DurationSeconds);
            }
        }
        Advance(now, settings);
    }
    private void Add(List<Pending> queue, CompactMessage message, DateTimeOffset ready, int capacity, bool replace)
    {
        if (queue.Count == capacity) { Omit(); if (!replace) return; queue.RemoveAt(0); }
        queue.Add(new(message, ready));
    }
    private void Omit() { Omitted++; Dropped?.Invoke(1); }
    public bool Advance(DateTimeOffset now, AppSettings settings)
    {
        var changed = false;
        foreach (var queue in new[] { _gifts, _guards, _sc, _follows })
            while (queue.Count > 0 && queue[0].Message.Received.AddSeconds(ReferenceEquals(queue, _gifts) || ReferenceEquals(queue, _follows) ? 60 : 300) <= now)
            { queue.RemoveAt(0); Omit(); changed = true; }
        CompactMessage? Next(CompactMessage? current, List<Pending>[] queues, double fallback, ref DateTimeOffset nextAt, double interval, bool trade)
        {
            if (current?.Expires > now) return current;
            if (current is not null) changed = true;
            if (now < nextAt) return null;
            foreach (var queue in queues)
            {
                if (queue.Count == 0 || queue[0].Ready > now) continue;
                var item = queue[0].Message; queue.RemoveAt(0);
                item.Expires = now.AddSeconds(trade ? settings.PriceDurations.Seconds(item.Value) : fallback);
                nextAt = now.AddSeconds(interval); changed = true; return item;
            }
            return null;
        }
        Gift = Next(Gift, [_guards, _gifts], 3, ref _nextGift, OverlayRegions.Get(settings.Display, OverlayRegionKind.Gift).DisplayIntervalSeconds, true);
        SuperChat = Next(SuperChat, [_sc], 10, ref _nextSc, OverlayRegions.Get(settings.Display, OverlayRegionKind.SuperChat).DisplayIntervalSeconds, true);
        Follow = Next(Follow, [_follows], settings.Compact.FollowSeconds, ref _nextEntry, 0, false);
        if (Entry?.Expires <= now) { Entry = null; changed = true; }
        return changed;
    }
    public DateTimeOffset? NextWake()
    {
        DateTimeOffset? due = null;
        void Include(DateTimeOffset value) { if (due is null || value < due) due = value; }
        foreach (var item in new[] { Gift, SuperChat, Follow, Entry }) if (item?.Expires is { } expiry) Include(expiry);
        foreach (var queue in new[] { _gifts, _guards, _sc, _follows })
        {
            if (queue.Count == 0) continue;
            Include(queue[0].Message.Received.AddSeconds(ReferenceEquals(queue, _gifts) || ReferenceEquals(queue, _follows) ? 60 : 300));
            var current = ReferenceEquals(queue, _sc) ? SuperChat : ReferenceEquals(queue, _follows) ? Follow : Gift;
            if (current is not null) continue;
            var interval = ReferenceEquals(queue, _sc) ? _nextSc : ReferenceEquals(queue, _follows) ? _nextEntry : _nextGift;
            Include(queue[0].Ready > interval ? queue[0].Ready : interval);
        }
        return due;
    }
    public void Clear()
    {
        _rows.Clear(); _gifts.Clear(); _guards.Clear(); _sc.Clear(); _follows.Clear();
        Gift = SuperChat = Entry = Follow = null; _nextGift = _nextSc = _nextEntry = default; Omitted = 0;
    }
}
