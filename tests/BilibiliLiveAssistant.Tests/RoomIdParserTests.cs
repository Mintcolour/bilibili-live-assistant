using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Tests;

public class RoomIdParserTests
{
    [Theory]
    [InlineData("12722549", 12722549)]
    [InlineData("https://live.bilibili.com/12345", 12345)]
    [InlineData("https://live.bilibili.com/12345?from=search", 12345)]
    public void ParsesRoomId(string input, int expected) => Assert.Equal(expected, RoomIdParser.Parse(input));

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("live.bilibili.com/1")]
    public void RejectsInvalidRoomId(string input) => Assert.Null(RoomIdParser.Parse(input));
}
