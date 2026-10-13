namespace BilibiliLiveAssistant.Core;

public sealed record LiveEmote(string Token, string Url, int Width = 24, int Height = 24, bool IsDynamic = false);

public sealed record LiveEntryEffect(string BackgroundUrl = "", string TextColor = "#FFFFFF", string HighlightColor = "#FFFFFF", string BadgeUrl = "");

public sealed record LiveMessageAppearance
{
    public string AvatarUrl { get; init; } = string.Empty;
    public string AvatarFrameUrl { get; init; } = string.Empty;
    public string UsernameColor { get; init; } = string.Empty;
    public string MedalName { get; init; } = string.Empty;
    public int MedalLevel { get; init; }
    public string MedalColor { get; init; } = "#527EBE";
    public int GuardLevel { get; init; }
    public int WealthLevel { get; init; }
    public bool IsAdmin { get; init; }
    public string InteractionLabel { get; init; } = string.Empty;
    public string InteractionIconUrl { get; init; } = string.Empty;
    public string InteractionHint { get; init; } = string.Empty;
    public LiveEntryEffect? EntryEffect { get; init; }
    public LiveEmote? FullEmote { get; init; }
    public IReadOnlyList<LiveEmote> Emotes { get; init; } = Array.Empty<LiveEmote>();
}

public static class LiveEntryStyles
{
    public static string Accent(LiveMessageAppearance? appearance) => appearance?.GuardLevel switch
    { 1 => "#BA714C", 2 => "#9C72CF", _ => "#478ACA" };
    public static string UsernameColor(LiveMessageAppearance? appearance, string fallback) =>
        appearance?.EntryEffect is { } effect ? effect.HighlightColor :
        appearance?.UsernameColor is { Length: > 0 } color ? color : appearance?.GuardLevel is >= 1 and <= 3 ? "#80C9FF" : fallback;
}

public readonly record struct MessageSegment(string Text, LiveEmote? Emote = null);

public static class RichMessageSegments
{
    public static IEnumerable<MessageSegment> Split(string text, LiveMessageAppearance? appearance)
    {
        if (appearance?.FullEmote is { } full) { yield return new(text, full); yield break; }
        var emotes = appearance?.Emotes;
        if (emotes is null || emotes.Count == 0) { yield return new(text); yield break; }
        var offset = 0;
        var pieces = 0;
        while (offset < text.Length && pieces++ < 128)
        {
            var found = -1;
            LiveEmote? match = null;
            foreach (var emote in emotes)
            {
                if (string.IsNullOrEmpty(emote.Token)) continue;
                var index = text.IndexOf(emote.Token, offset, StringComparison.Ordinal);
                if (index >= 0 && (found < 0 || index < found || index == found && emote.Token.Length > match!.Token.Length))
                { found = index; match = emote; }
            }
            if (found < 0) break;
            if (found > offset) yield return new(text[offset..found]);
            yield return new(match!.Token, match);
            offset = found + match.Token.Length;
        }
        if (offset < text.Length) yield return new(text[offset..]);
    }
}
