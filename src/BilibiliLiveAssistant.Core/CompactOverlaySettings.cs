using System.Globalization;

namespace BilibiliLiveAssistant.Core;

public sealed record PriceDurationTier(decimal FromYuan, double Seconds);

public sealed record OverlayPriceDurations
{
    public IReadOnlyList<PriceDurationTier> SuperChat { get; init; } = [new(0, 10), new(50, 15), new(100, 20), new(500, 30), new(1000, 40), new(2000, 50)];
    public IReadOnlyList<PriceDurationTier> Gift { get; init; } = [new(0, 3), new(1, 5), new(10, 10), new(50, 15), new(100, 20), new(500, 30)];
    public double UnknownGuardSeconds { get; init; } = 30;
    public OverlayPriceDurations Normalize() => this with
    {
        SuperChat = Tiers(SuperChat, new OverlayPriceDurations().SuperChat, 10, 50),
        Gift = Tiers(Gift, new OverlayPriceDurations().Gift, 3, 30),
        UnknownGuardSeconds = Finite(UnknownGuardSeconds, 30, 3, 300),
    };
    private static IReadOnlyList<PriceDurationTier> Tiers(IReadOnlyList<PriceDurationTier>? values, IReadOnlyList<PriceDurationTier> fallback, double min, double max)
    {
        var result = values?.Where(t => t is not null && t.FromYuan >= 0 && t.FromYuan <= 100000000 && double.IsFinite(t.Seconds))
            .Take(16).GroupBy(t => t.FromYuan).Select(g => g.Last() with { Seconds = Math.Clamp(g.Last().Seconds, min, max) }).OrderBy(t => t.FromYuan).ToArray();
        return result is not { Length: > 0 } || result[0].FromYuan != 0 ? fallback : result;
    }
    internal static double Finite(double value, double fallback, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    public double Seconds(LiveEvent value)
    {
        var amount = value.HistoryFields?.AmountMilliYuan;
        // Only explicit prices, never display text or estimated guard subscription prices.
        if (amount is null && value.Type == LiveEventType.SuperChat && value.Extra.GetValueOrDefault("price") is { } raw
            && decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out var yuan)
            && yuan >= 0 && yuan <= long.MaxValue / 1000m) amount = (long)(yuan * 1000);
        if (amount is null) return value.Type == LiveEventType.Guard ? UnknownGuardSeconds : value.Type == LiveEventType.SuperChat ? 10 : 3;
        var tiers = value.Type == LiveEventType.SuperChat ? SuperChat : Gift;
        return tiers.LastOrDefault(t => t.FromYuan <= amount.Value / 1000m)?.Seconds ?? (value.Type == LiveEventType.SuperChat ? 10 : 3);
    }
}

public sealed record CompactOverlaySettings
{
    public bool DesktopEnabled { get; init; } = true;
    public bool ObsEnabled { get; init; }
    public int ObsPort { get; init; } = 17865;
    public int ObsWidth { get; init; } = 480;
    public int ObsHeight { get; init; } = 800;
    public int Width { get; init; } = 480;
    public int Height { get; init; } = 760;
    public int? X { get; init; }
    public int? Y { get; init; }
    public int FontSize { get; init; } = 18;
    public int EmoteSize { get; init; } = 28;
    public double Opacity { get; init; } = 1;
    public string BackgroundColor { get; init; } = "#242833";
    public double BackgroundOpacity { get; init; }
    public bool ShowStatus { get; init; } = true;
    public bool ShowSuperChat { get; init; } = true;
    public bool ShowGift { get; init; } = true;
    public bool ShowList { get; init; } = true;
    public bool ListAnimationEnabled { get; init; } = true;
    public int ListAnimationDurationMs { get; init; } = 200;
    public bool ShowEntry { get; init; } = true;
    public bool ShowFollow { get; init; } = true;
    public int SuperChatLines { get; init; } = 6;
    public double FollowSeconds { get; init; } = 5;
    public ObsThemeSettings ObsTheme { get; init; } = new();
    public CompactOverlaySettings Normalize() => this with
    {
        ObsPort = Math.Clamp(ObsPort, 1024, 65535), ObsWidth = Math.Clamp(ObsWidth, 240, 1920), ObsHeight = Math.Clamp(ObsHeight, 320, 2160),
        Width = Math.Clamp(Width, 240, 1920), Height = Math.Clamp(Height, 320, 2160), FontSize = Math.Clamp(FontSize, 12, 48), EmoteSize = Math.Clamp(EmoteSize, 16, 128),
        Opacity = OverlayPriceDurations.Finite(Opacity, 1, 0.1, 1), BackgroundOpacity = OverlayPriceDurations.Finite(BackgroundOpacity, 0, 0, 1),
        BackgroundColor = OverlayRegionSettings.IsColor(BackgroundColor) ? BackgroundColor : "#242833", SuperChatLines = Math.Clamp(SuperChatLines, 1, 12),
        FollowSeconds = OverlayPriceDurations.Finite(FollowSeconds, 5, 1, 300),
        ListAnimationDurationMs = Math.Clamp(ListAnimationDurationMs, 100, 500),
        ObsTheme = (ObsTheme ?? new()).Normalize(),
    };
}
