namespace BilibiliLiveAssistant.Core;

public sealed record DashboardSettings
{
    public const int MaxInteractionCapacity = 15;
    public string Preset { get; init; } = "深色简约";
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public int FontSize { get; init; } = 18;
    public int UsernameFontSize { get; init; } = 15;
    public string TextColor { get; init; } = "#F1F3F8";
    public string UsernameColor { get; init; } = "#C7D2F0";
    public string BackgroundColor { get; init; } = "#242833";
    public double TextOpacity { get; init; } = 1;
    public double BackgroundOpacity { get; init; } = 0.85;
    public int LineSpacing { get; init; } = 4;
    public int MessageSpacing { get; init; } = 8;
    public int Padding { get; init; } = 12;
    public int CornerRadius { get; init; } = 8;
    public int AvatarSize { get; init; } = 36;
    public int BadgeSize { get; init; } = 12;
    public int EmoteSize { get; init; } = 28;
    public int LargeEmoteHeight { get; init; } = 100;
    public bool ShowAvatars { get; init; } = true;
    public bool ShowEntryAvatars { get; init; } = true;
    public bool ShowMedals { get; init; } = true;
    public bool ShowIdentity { get; init; } = true;
    public bool AnimateEmotes { get; init; }
    public bool CompactMessages { get; init; } = true;
    public double InteractionPanelRatio { get; init; } = 0.22;
    public double RankPanelRatio { get; init; } = 0.4;
    public bool RankPanelCollapsed { get; init; }
    public int RankRefreshSeconds { get; init; } = 60;
    public DashboardRankKind RankKind { get; init; }
    public int RefreshIntervalMs { get; init; } = 250;
    public int DanmakuCapacity { get; init; } = 500;
    public int GiftCapacity { get; init; } = 200;
    public int SuperChatCapacity { get; init; } = 100;
    public int InteractionCapacity { get; init; } = MaxInteractionCapacity;
    public double DanmakuColumnWidth { get; init; } = 2;
    public double SuperChatColumnWidth { get; init; } = 1;
    public double GiftColumnWidth { get; init; } = 1;

    // Layout, history limits and refresh cadence do not change a message's
    // visuals. Preserve decoded images when only those settings are saved.
    public bool HasSameMessageStyle(DashboardSettings other) =>
        FontFamily == other.FontFamily && FontSize == other.FontSize && UsernameFontSize == other.UsernameFontSize &&
        TextColor == other.TextColor && UsernameColor == other.UsernameColor && BackgroundColor == other.BackgroundColor &&
        TextOpacity == other.TextOpacity && BackgroundOpacity == other.BackgroundOpacity &&
        LineSpacing == other.LineSpacing && MessageSpacing == other.MessageSpacing && Padding == other.Padding && CornerRadius == other.CornerRadius &&
        AvatarSize == other.AvatarSize && BadgeSize == other.BadgeSize && EmoteSize == other.EmoteSize && LargeEmoteHeight == other.LargeEmoteHeight &&
        ShowAvatars == other.ShowAvatars && ShowEntryAvatars == other.ShowEntryAvatars && ShowMedals == other.ShowMedals && ShowIdentity == other.ShowIdentity &&
        AnimateEmotes == other.AnimateEmotes && CompactMessages == other.CompactMessages;

    // Older entry events have no interaction kind. Explicit follows, shares and
    // likes keep the global avatar policy; only room entries use this switch.
    public bool ShowsAvatar(LiveEvent message) => ShowAvatars && (ShowEntryAvatars || !LiveInteractions.IsEntry(message));

    public DashboardSettings Normalize() => this with
    {
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Microsoft YaHei UI" : FontFamily.Trim()[..Math.Min(FontFamily.Trim().Length, 128)],
        FontSize = Math.Clamp(FontSize, 12, 48), UsernameFontSize = Math.Clamp(UsernameFontSize, 12, 36),
        TextColor = Color(TextColor, "#F1F3F8"), UsernameColor = Color(UsernameColor, "#C7D2F0"), BackgroundColor = Color(BackgroundColor, "#242833"),
        TextOpacity = Finite(TextOpacity, 1, 0.2, 1), BackgroundOpacity = Finite(BackgroundOpacity, 0.85, 0, 1),
        LineSpacing = Math.Clamp(LineSpacing, 0, 24), MessageSpacing = Math.Clamp(MessageSpacing, 0, 32),
        Padding = Math.Clamp(Padding, 0, 32), CornerRadius = Math.Clamp(CornerRadius, 0, 32),
        AvatarSize = Math.Clamp(AvatarSize, 24, 64), BadgeSize = Math.Clamp(BadgeSize, 10, 20),
        EmoteSize = Math.Clamp(EmoteSize, 16, 64), LargeEmoteHeight = Math.Clamp(LargeEmoteHeight, 40, 160),
        RefreshIntervalMs = Math.Clamp(RefreshIntervalMs, 100, 2000),
        InteractionPanelRatio = Finite(InteractionPanelRatio, 0.22, 0.12, 0.5),
        RankPanelRatio = Finite(RankPanelRatio, 0.4, 0.2, 0.8), RankRefreshSeconds = Math.Clamp(RankRefreshSeconds, 15, 600),
        RankKind = Enum.IsDefined(RankKind) ? RankKind : DashboardRankKind.Energy,
        DanmakuCapacity = Math.Clamp(DanmakuCapacity, 50, 2000), GiftCapacity = Math.Clamp(GiftCapacity, 20, 1000),
        SuperChatCapacity = Math.Clamp(SuperChatCapacity, 20, 500), InteractionCapacity = Math.Clamp(InteractionCapacity, 1, MaxInteractionCapacity),
        DanmakuColumnWidth = Finite(DanmakuColumnWidth, 2, 0.3, 6), SuperChatColumnWidth = Finite(SuperChatColumnWidth, 1, 0.3, 6), GiftColumnWidth = Finite(GiftColumnWidth, 1, 0.3, 6),
    };
    private static string Color(string value, string fallback) => OverlayRegionSettings.IsColor(value) ? value : fallback;
    private static double Finite(double value, double fallback, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    public static DashboardSettings ForPreset(string preset, DashboardSettings current) => preset switch
    {
        "圆角气泡" => current with { Preset = preset, CompactMessages = false, BackgroundColor = "#30364A", BackgroundOpacity = 0.95, CornerRadius = 16, Padding = 14, MessageSpacing = 10, LineSpacing = 6 },
        "透明紧凑" => current with { Preset = preset, CompactMessages = true, BackgroundOpacity = 0, CornerRadius = 0, Padding = 6, MessageSpacing = 3, LineSpacing = 2, FontSize = 16, AvatarSize = 28 },
        _ => current with { Preset = "深色简约", CompactMessages = true, BackgroundColor = "#242833", BackgroundOpacity = 0.85, CornerRadius = 8, Padding = 12, MessageSpacing = 8, LineSpacing = 4, FontSize = 18, AvatarSize = 36 },
    };
}
