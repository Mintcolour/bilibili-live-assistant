using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Tests;

public sealed class LiveEventBufferTests
{
    private static LiveEvent Message(int number, LiveEventType type = LiveEventType.Danmaku) => new(type, "user", number.ToString(), "#FFFFFF");

    [Fact]
    public void BurstKeepsNewestEventsAndNeverExceedsCapacity()
    {
        var buffer = new LiveEventBuffer(256);
        for (var i = 0; i < 10000; i++) buffer.Enqueue(Message(i));
        Assert.Equal(256, buffer.Count);
        Assert.Equal(9744, buffer.DroppedCount);
        Assert.Equal(Enumerable.Range(9744, 256).Select(i => i.ToString()), buffer.Drain().Select(e => e.Content));
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void OverloadPreservesAndDequeuesPaidEventsFirst()
    {
        var buffer = new LiveEventBuffer(3);
        buffer.Enqueue(Message(1, LiveEventType.SuperChat));
        buffer.Enqueue(Message(2));
        buffer.Enqueue(Message(3, LiveEventType.Guard));
        buffer.Enqueue(Message(4));
        Assert.True(buffer.TryDequeue(out var first));
        Assert.Equal(LiveEventType.SuperChat, first!.Type);
        Assert.True(buffer.TryDequeue(out var second));
        Assert.Equal(LiveEventType.Guard, second!.Type);
        Assert.Equal("4", Assert.Single(buffer.Drain()).Content);
    }

    [Fact]
    public void OrdinaryTrafficDoesNotEvictAQueueFullOfPaidMessages()
    {
        var buffer = new LiveEventBuffer(2);
        buffer.Enqueue(Message(1, LiveEventType.SuperChat));
        buffer.Enqueue(Message(2, LiveEventType.Guard));
        buffer.Enqueue(Message(3));
        Assert.Equal(new[] {"1", "2"}, buffer.Drain().Select(e => e.Content));
        Assert.Equal(1, buffer.DroppedCount);
    }

    [Fact]
    public async Task ConcurrentProducersRemainBounded()
    {
        var buffer = new LiveEventBuffer(256);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(producer => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++) buffer.Enqueue(Message(producer * 1000 + i));
        })));
        Assert.Equal(256, buffer.Count);
        Assert.Equal(4000 - 256, buffer.DroppedCount);
    }

    [Fact]
    public void HistoryBatchMaintainsOrderLimitAndSingleNotification()
    {
        var history = new LiveEventHistory(200);
        var notifications = 0;
        history.CollectionChanged += (_, _) => notifications++;
        history.PrependBatch(Enumerable.Range(0, 100).Select(i => Message(i)).ToArray());
        history.PrependBatch(Enumerable.Range(100, 1000).Select(i => Message(i)).ToArray());
        Assert.Equal(2, notifications);
        Assert.Equal(200, history.Count);
        Assert.Equal(Enumerable.Range(900, 200).Reverse().Select(i => i.ToString()), history.Select(e => e.Content));
        history.PrependBatch([]);
        Assert.Equal(2, notifications);
    }
}
