using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Tests;

public class CompactTimelineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
    private static LiveEvent Entry(int? kind = 1) => new(LiveEventType.Enter, "观众", "互动", "#FFFFFF")
    { HistoryFields = kind is null ? null : new(InteractionKind: kind) };
    private static LiveEvent Danmaku(string body) => new(LiveEventType.Danmaku, "观众", body, "#FFFFFF");

    [Fact]
    public void EntryFloodDoesNotDisplaceMessagesOrSuppressFooter()
    {
        var timeline = new CompactTimeline();
        var settings = new AppSettings();
        var messages = Enumerable.Range(0, 100).Select(i => Danmaku(i.ToString())).ToArray();
        timeline.AddBatch(messages, Now, settings);
        var entries = Enumerable.Range(0, 200).Select(_ => Entry()).ToArray();
        timeline.AddBatch(entries, Now, settings);

        Assert.Equal(messages, timeline.Rows.Select(row => row.Value));
        Assert.Same(entries[^1], timeline.Entry!.Value);
        Assert.Equal(0, timeline.Omitted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void EntryCanJoinListIndependentlyOfFooterSwitch(int? kind)
    {
        var timeline = new CompactTimeline();
        var entry = Entry(kind);
        var message = Danmaku("消息");
        var settings = new AppSettings { Compact = new() { ShowEntry = false, ShowEntryInList = true } };
        timeline.AddBatch([message, entry], Now, settings);

        Assert.Equal(new[] { message, entry }, timeline.Rows.Select(row => row.Value));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void OtherInteractionsRemainInList(int kind)
    {
        var timeline = new CompactTimeline();
        var interaction = Entry(kind);
        timeline.AddBatch([interaction], Now, new AppSettings());

        Assert.Same(interaction, Assert.Single(timeline.Rows).Value);
        Assert.Null(timeline.Entry);
        if (LiveInteractions.IsFollow(interaction)) Assert.Same(interaction, timeline.Follow!.Value);
    }

    [Fact]
    public void DisablingRemovesExistingEntriesImmediatelyAndReenablingOnlyAddsNewEntries()
    {
        var timeline = new CompactTimeline();
        var enabled = new AppSettings { Compact = new() { ShowEntryInList = true } };
        var disabled = enabled with { Compact = enabled.Compact with { ShowEntryInList = false } };
        var message = Danmaku("保留的弹幕");
        var entry = Entry();
        var follow = Entry(2);
        timeline.AddBatch([message, entry, follow], Now, enabled);
        var footer = timeline.Entry;

        Assert.True(timeline.Advance(Now, disabled));
        Assert.Equal(new[] { message, follow }, timeline.Rows.Select(row => row.Value));
        Assert.Same(footer, timeline.Entry);
        timeline.Advance(Now, enabled);
        Assert.Equal(2, timeline.Rows.Count);
        var nextEntry = Entry();
        timeline.AddBatch([nextEntry], Now, enabled);
        Assert.Same(nextEntry, timeline.Rows[^1].Value);
    }
}
