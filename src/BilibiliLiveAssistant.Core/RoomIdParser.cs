using System.Text.RegularExpressions;

namespace BilibiliLiveAssistant.Core;

public static partial class RoomIdParser
{
    [GeneratedRegex(@"(?:live\.bilibili\.com/)?(\d{2,10})", RegexOptions.IgnoreCase)]
    private static partial Regex RoomIdRegex();

    public static int? Parse(string? text)
    {
        var match = RoomIdRegex().Match(text?.Trim() ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var roomId)
            ? roomId
            : null;
    }
}
