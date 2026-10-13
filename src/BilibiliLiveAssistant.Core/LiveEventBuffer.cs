namespace BilibiliLiveAssistant.Core;

/// <summary>Bounded, thread-safe handoff from network callbacks to the UI.</summary>
public sealed class LiveEventBuffer(int capacity = 256)
{
    private readonly object _gate = new();
    private readonly LinkedList<LiveEvent> _items = new();
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private long _dropped;

    public int Count { get { lock (_gate) return _items.Count; } }
    public long DroppedCount { get { lock (_gate) return _dropped; } }

    public void Enqueue(LiveEvent value)
    {
        lock (_gate)
        {
            if (_items.Count == _capacity)
            {
                // SC > gifts/guards > ordinary events. A gift burst must not
                // evict an SC merely because it shares the network handoff.
                var candidate = _items.First!;
                var priority = Priority(candidate.Value);
                for (var node = candidate.Next; node is not null && priority > 0; node = node.Next)
                {
                    var nextPriority = Priority(node.Value);
                    if (nextPriority < priority) { candidate = node; priority = nextPriority; }
                }
                if (priority > Priority(value)) { _dropped++; return; }
                _items.Remove(candidate);
                _dropped++;
            }
            _items.AddLast(value);
        }
    }

    public LiveEvent[] Drain()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return [];
            var batch = _items.ToArray();
            _items.Clear();
            return batch;
        }
    }

    public bool TryDequeue(out LiveEvent? value)
    {
        lock (_gate)
        {
            var next = _items.First;
            for (var candidate = next; candidate is not null; candidate = candidate.Next)
                if (IsPriority(candidate.Value)) { next = candidate; break; }
            if (next is null) { value = null; return false; }
            value = next.Value;
            _items.Remove(next);
            return true;
        }
    }

    public void Clear() { lock (_gate) _items.Clear(); }
    public static bool IsPriority(LiveEvent value) => value.Type is LiveEventType.Gift or LiveEventType.Guard or LiveEventType.SuperChat;
    private static int Priority(LiveEvent value) => value.Type == LiveEventType.SuperChat ? 2 : IsPriority(value) ? 1 : 0;
}
