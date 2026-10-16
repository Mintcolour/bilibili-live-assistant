namespace BilibiliLiveAssistant.Core;

public static class LiveInteractions
{
    public static bool IsEntry(LiveEvent value) => value.Type == LiveEventType.Enter
        && value.HistoryFields?.InteractionKind is null or 1;

    // Trust the parsed upstream interaction kind, never a nickname or body match.
    public static bool IsFollow(LiveEvent value) => value.Type == LiveEventType.Enter
        && value.HistoryFields?.InteractionKind is 2 or 4 or 5;

    public static string FollowLabel(LiveEvent value) => value.HistoryFields?.InteractionKind switch
    { 4 => "特别关注", 5 => "互粉", _ => "新关注" };
}
