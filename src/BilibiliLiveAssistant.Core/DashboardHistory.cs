using System.Diagnostics;

namespace BilibiliLiveAssistant.Core;

public sealed class DashboardRecord(LiveEvent value)
{
    public Guid Id { get; } = Guid.NewGuid();
    public LiveEvent Event { get; private set; } = value;
    public long Revision { get; private set; }
    internal long CreatedAt { get; } = Stopwatch.GetTimestamp();
    public bool IsRead { get; internal set; }
    public bool IsThanked { get; internal set; }

    internal void Merge(LiveEvent value)
    {
        Event = GiftAggregation.Combine(Event, value);
        Revision++;
        IsRead = false;
        IsThanked = false;
    }
}

/// <summary>UI-thread-owned histories; overlay expiry never removes dashboard records.</summary>
public sealed class DashboardHistory
{
    public List<DashboardRecord> Danmaku { get; } = new();
    public List<DashboardRecord> Gifts { get; } = new();
    public List<DashboardRecord> SuperChats { get; } = new();
    public List<DashboardRecord> Interactions { get; } = new();
    public long Version { get; private set; }
    public long EvictedUnread { get; private set; }
    public event Action? Changed;

    public void AddBatch(IReadOnlyList<LiveEvent> batch, DashboardSettings settings, double giftMergeSeconds = 3)
    {
        giftMergeSeconds = double.IsFinite(giftMergeSeconds) ? Math.Clamp(giftMergeSeconds, 0, 30) : 3;
        var changed = false;
        foreach (var e in batch)
        {
            var list = e.Type switch
            {
                LiveEventType.Danmaku => Danmaku, LiveEventType.Gift or LiveEventType.Guard => Gifts,
                LiveEventType.SuperChat => SuperChats, LiveEventType.Enter => Interactions, _ => null,
            };
            if (list is null) continue;
            if (list == Gifts && giftMergeSeconds > 0)
            {
                var now = Stopwatch.GetTimestamp();
                DashboardRecord? match = null;
                foreach (var record in Gifts)
                {
                    // Newest first; merging never extends a group's first-receipt deadline.
                    if (Stopwatch.GetElapsedTime(record.CreatedAt, now).TotalSeconds >= giftMergeSeconds) break;
                    if (GiftAggregation.CanMerge(record.Event, e)) { match = record; break; }
                }
                if (match is not null) { match.Merge(e); changed = true; continue; }
            }
            list.Insert(0, new DashboardRecord(e));
            if (list == Gifts) TrimList(Gifts, settings.GiftCapacity, true);
            changed = true;
        }
        Trim(settings, false);
        if (changed) Notify();
    }
    public void Trim(DashboardSettings settings, bool notify = true)
    {
        var changed = TrimList(Danmaku, settings.DanmakuCapacity, false) | TrimList(Gifts, settings.GiftCapacity, true)
            | TrimList(SuperChats, settings.SuperChatCapacity, true) | TrimInteractions(settings.InteractionCapacity);
        if (changed && notify) Notify();
    }
    private bool TrimInteractions(int capacity)
    {
        capacity = Math.Clamp(capacity, 1, DashboardSettings.MaxInteractionCapacity);
        if (Interactions.Count <= capacity) return false;
        // Arrival order is newest first. Always keep the most recent entries,
        // regardless of read status, rather than retaining older unread entries.
        Interactions.RemoveRange(capacity, Interactions.Count - capacity);
        return true;
    }
    private bool TrimList(List<DashboardRecord> list, int capacity, bool trackUnread)
    {
        var changed = false;
        while (list.Count > capacity)
        {
            var index = list.FindLastIndex(x => x.IsThanked || x.IsRead);
            if (index < 0) index = list.Count - 1;
            if (trackUnread && !list[index].IsRead) EvictedUnread++;
            list.RemoveAt(index); changed = true;
        }
        return changed;
    }
    public void Mark(DashboardRecord record, bool thanked)
    {
        record.IsRead = true;
        if (thanked) record.IsThanked = true;
        Notify();
    }
    public void MarkAllRead(bool gifts)
    {
        foreach (var record in gifts ? Gifts : SuperChats) record.IsRead = true;
        Notify();
    }
    public void Clear()
    {
        Danmaku.Clear(); Gifts.Clear(); SuperChats.Clear(); Interactions.Clear(); EvictedUnread = 0; Notify();
    }
    private void Notify() { Version++; Changed?.Invoke(); }
}
