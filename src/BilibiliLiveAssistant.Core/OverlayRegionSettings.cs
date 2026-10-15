namespace BilibiliLiveAssistant.Core;

public enum OverlayRegionKind { Danmaku, Gift, SuperChat, Interaction, Status }
public enum OverlayDisplayMode { Scrolling, List, Grid, Cards, Panel }

public sealed record OverlayRegionSettings
{
    public OverlayDisplayMode Mode { get; init; }
    public int ModeVersion { get; init; }
    public double GuardDurationSeconds { get; init; } = 30;
    public int CardHeight { get; init; } = 140;
    public int CardColumns { get; init; } = 2;
    public string BackgroundColor { get; init; } = "#242833";
    public double BackgroundOpacity { get; init; } = 0.9;
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public int FontSize { get; init; } = 28;
    public int EmoteSize { get; init; } = 36;
    public int LargeEmoteHeight { get; init; } = 100;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Outline { get; init; } = true;
    public string Color { get; init; } = "#FFFFFF";
    public double Opacity { get; init; } = 0.85;
    public int UpdateIntervalMs { get; init; } = 100;
    public double DurationSeconds { get; init; } = 6;
    public double MergeSeconds { get; init; } = 3;
    public int MaxVisible { get; init; } = 3;
    public int PlaybackVersion { get; init; }
    public int ScrollSpeed { get; init; }
    public int BroadcastIntervalSeconds { get; init; } = 30;
    public int RankCount { get; init; } = 3;
    public bool ShowAudienceStats { get; init; } = true;
    public int? X { get; init; }
    public int? Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    public OverlayRegionSettings Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : OverlayDisplayMode.Scrolling,
        GuardDurationSeconds = double.IsFinite(GuardDurationSeconds) ? Math.Clamp(GuardDurationSeconds, 5, 300) : 30,
        CardHeight = Math.Clamp(CardHeight, 100, 400), CardColumns = Math.Clamp(CardColumns, 1, 4),
        BackgroundColor = IsColor(BackgroundColor) ? BackgroundColor : "#242833",
        BackgroundOpacity = double.IsFinite(BackgroundOpacity) ? Math.Clamp(BackgroundOpacity, 0, 1) : 0.9,
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Microsoft YaHei UI" : FontFamily.Trim(),
        FontSize = Math.Clamp(FontSize, 12, 72),
        EmoteSize = Math.Clamp(EmoteSize, 16, 128),
        LargeEmoteHeight = Math.Clamp(LargeEmoteHeight, 40, 256),
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.1, 1) : 0.85,
        UpdateIntervalMs = Math.Clamp(UpdateIntervalMs, 50, 2000),
        DurationSeconds = double.IsFinite(DurationSeconds) ? Math.Clamp(DurationSeconds, 1, 300) : 6,
        MergeSeconds = double.IsFinite(MergeSeconds) ? Math.Clamp(MergeSeconds, 0, 30) : 3,
        MaxVisible = Math.Clamp(MaxVisible, 1, 64),
        ScrollSpeed = ScrollSpeed <= 0 ? 0 : Math.Clamp(ScrollSpeed, 30, 500),
        BroadcastIntervalSeconds = Math.Clamp(BroadcastIntervalSeconds, 5, 600),
        RankCount = Math.Clamp(RankCount, 1, 10),
        Width = Width <= 0 ? 0 : Math.Clamp(Width, 160, 7680),
        Height = Height <= 0 ? 0 : Math.Clamp(Height, 80, 4320),
        Color = IsColor(Color) ? Color.ToUpperInvariant() : "#FFFFFF",
    };

    public static bool IsColor(string? color) => color is { Length: 7 } && color[0] == '#'
        && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}

public static class OverlayRegions
{
    public static string Title(OverlayRegionKind kind) => kind switch
    {
        OverlayRegionKind.Danmaku => "普通弹幕", OverlayRegionKind.Gift => "礼物 / 上舰",
        OverlayRegionKind.SuperChat => "醒目留言 SC", OverlayRegionKind.Interaction => "进房 / 关注",
        _ => "人气 / 高能榜",
    };

    // Missing region records preserve the old appearance without a destructive migration.
    public static OverlayRegionSettings Get(DisplaySettings display, OverlayRegionKind kind)
    {
        var region = (kind switch
    {
        OverlayRegionKind.Danmaku => display.DanmakuRegion ?? new() { FontSize = display.FontSize, Opacity = display.Opacity, Color = display.DanmakuColor, MaxVisible = 64 },
        OverlayRegionKind.Gift => display.GiftRegion ?? new() { FontSize = 22, Color = display.GiftColor, UpdateIntervalMs = 200 },
        OverlayRegionKind.SuperChat => display.SuperChatRegion ?? new() { FontSize = 24, Color = display.SuperChatColor, DurationSeconds = 15, MaxVisible = 2 },
        OverlayRegionKind.Interaction => display.InteractionRegion ?? new() { FontSize = 18, Color = display.EnterColor, DurationSeconds = 5, UpdateIntervalMs = 500, MaxVisible = 3 },
        _ => display.StatusRegion ?? new() { FontSize = 16, Color = display.OnlineColor, MaxVisible = 2, UpdateIntervalMs = 500 },
        }).Normalize();
        if (kind == OverlayRegionKind.Interaction && region.PlaybackVersion == 0)
            region = region with { MaxVisible = 3, DurationSeconds = region.DurationSeconds == 3 ? 5 : region.DurationSeconds,
                Height = region.Height == 100 ? 180 : region.Height };
        if (region.ModeVersion == 0) region = region with
        {
            Mode = kind switch { OverlayRegionKind.Gift or OverlayRegionKind.SuperChat => OverlayDisplayMode.Cards,
                OverlayRegionKind.Status => OverlayDisplayMode.Panel, _ => OverlayDisplayMode.Scrolling },
            DurationSeconds = kind == OverlayRegionKind.Gift ? 20 : kind == OverlayRegionKind.SuperChat ? 60 : region.DurationSeconds,
            MaxVisible = kind == OverlayRegionKind.Gift ? 3 : kind == OverlayRegionKind.SuperChat ? 2 : region.MaxVisible,
            CardHeight = kind == OverlayRegionKind.Gift ? 100 : kind == OverlayRegionKind.SuperChat ? 180 : region.CardHeight,
        };
        var allowed = Modes(kind); if (!allowed.Contains(region.Mode)) region = region with { Mode = allowed[0] };
        return region with { PlaybackVersion = 1, ModeVersion = 1,
            ScrollSpeed = region.ScrollSpeed > 0 ? region.ScrollSpeed : kind == OverlayRegionKind.Danmaku ? Math.Clamp(display.ScrollSpeed, 30, 500) : 120,
            DurationSeconds = kind is OverlayRegionKind.Gift or OverlayRegionKind.SuperChat ? Math.Max(5, region.DurationSeconds) : region.DurationSeconds,
            MaxVisible = kind == OverlayRegionKind.Interaction ? Math.Min(3, region.MaxVisible) : region.Mode == OverlayDisplayMode.Cards ? Math.Min(6, region.MaxVisible) : kind is OverlayRegionKind.SuperChat or OverlayRegionKind.Status ? 1 : region.MaxVisible };
    }
    public static OverlayDisplayMode[] Modes(OverlayRegionKind kind) => kind switch
    {
        OverlayRegionKind.Danmaku => [OverlayDisplayMode.Scrolling, OverlayDisplayMode.List, OverlayDisplayMode.Grid],
        OverlayRegionKind.Gift or OverlayRegionKind.SuperChat => [OverlayDisplayMode.Scrolling, OverlayDisplayMode.Cards],
        OverlayRegionKind.Status => [OverlayDisplayMode.Scrolling, OverlayDisplayMode.Panel], _ => [OverlayDisplayMode.Scrolling],
    };
    public static string ModeTitle(OverlayDisplayMode mode) => mode switch
    { OverlayDisplayMode.List => "固定列表", OverlayDisplayMode.Grid => "固定网格卡片", OverlayDisplayMode.Cards => "定时固定卡片", OverlayDisplayMode.Panel => "固定面板", _ => "滚动弹幕" };
    public static bool Stationary(DisplaySettings display, OverlayRegionKind kind) => kind != OverlayRegionKind.Interaction && Get(display, kind).Mode != OverlayDisplayMode.Scrolling;

    public static DisplaySettings With(DisplaySettings display, OverlayRegionKind kind, OverlayRegionSettings settings) => kind switch
    {
        OverlayRegionKind.Danmaku => display with { DanmakuRegion = settings },
        OverlayRegionKind.Gift => display with { GiftRegion = settings },
        OverlayRegionKind.SuperChat => display with { SuperChatRegion = settings },
        OverlayRegionKind.Interaction => display with { InteractionRegion = settings },
        _ => display with { StatusRegion = settings },
    };

    public static bool Enabled(DisplaySettings display, OverlayRegionKind kind) => kind switch
    {
        OverlayRegionKind.Gift => display.ShowGift, OverlayRegionKind.SuperChat => display.ShowSuperChat,
        OverlayRegionKind.Interaction => display.ShowEnter, OverlayRegionKind.Status => display.ShowOnline, _ => true,
    };
}
