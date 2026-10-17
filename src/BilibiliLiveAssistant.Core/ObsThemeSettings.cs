using System.Text;

namespace BilibiliLiveAssistant.Core;

// OBS-only appearance. Native compact panels retain their own renderer and shared time line.
public sealed record ObsThemeSettings
{
    public const int MaxCssBytes = 256 * 1024;
    public string Template { get; init; } = "compact";
    public bool UseSharedAppearance { get; init; } = true;
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public int FontSize { get; init; } = 18;
    public int AvatarSize { get; init; } = 28;
    public int EmoteSize { get; init; } = 28;
    public int Gap { get; init; } = 4;
    public int Padding { get; init; } = 6;
    public int Radius { get; init; } = 10;
    public int BorderWidth { get; init; }
    public string TextColor { get; init; } = "#F1F3F8";
    public string NameColor { get; init; } = "#C7D2F0";
    public string BackgroundColor { get; init; } = "#242833";
    public string BorderColor { get; init; } = "#6C7894";
    public double BackgroundOpacity { get; init; } = 0.75;
    public double Opacity { get; init; } = 1;
    public bool Shadow { get; init; } = true;
    public bool ShowAvatars { get; init; } = true;
    public bool ShowMedals { get; init; } = true;
    public bool ShowIdentity { get; init; } = true;
    public bool ShowStatus { get; init; } = true;
    public bool ShowSuperChat { get; init; } = true;
    public bool ShowGift { get; init; } = true;
    public bool ShowList { get; init; } = true;
    public bool ShowEntry { get; init; } = true;
    public bool ShowFollow { get; init; } = true;
    public string CustomCss { get; init; } = "";

    public ObsThemeSettings Normalize() => this with
    {
        Template = Template is "compact" or "text" or "bubble" or "card" ? Template : "compact",
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Microsoft YaHei UI" : FontFamily.Trim()[..Math.Min(FontFamily.Trim().Length, 128)],
        FontSize = Math.Clamp(FontSize, 12, 64), AvatarSize = Math.Clamp(AvatarSize, 12, 96), EmoteSize = Math.Clamp(EmoteSize, 16, 128),
        Gap = Math.Clamp(Gap, 0, 32), Padding = Math.Clamp(Padding, 0, 32), Radius = Math.Clamp(Radius, 0, 40), BorderWidth = Math.Clamp(BorderWidth, 0, 8),
        TextColor = Color(TextColor, "#F1F3F8"), NameColor = Color(NameColor, "#C7D2F0"), BackgroundColor = Color(BackgroundColor, "#242833"), BorderColor = Color(BorderColor, "#6C7894"),
        BackgroundOpacity = OverlayPriceDurations.Finite(BackgroundOpacity, .75, 0, 1), Opacity = OverlayPriceDurations.Finite(Opacity, 1, 0, 1),
        CustomCss = CustomCss is not null && Encoding.UTF8.GetByteCount(CustomCss) <= MaxCssBytes ? CustomCss : "",
    };
    private static string Color(string? value, string fallback) => OverlayRegionSettings.IsColor(value) ? value! : fallback;
}
