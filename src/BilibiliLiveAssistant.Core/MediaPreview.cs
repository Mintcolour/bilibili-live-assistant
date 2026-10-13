using System.Text.RegularExpressions;

namespace BilibiliLiveAssistant.Core;

public enum MediaKind { Bilibili, Steam }
public enum MediaPreviewMode { Inline, OnClick }
public sealed record MediaReference(MediaKind Kind, string Id)
{
    public string Key => $"{Kind}:{Id}";
    public Uri Page => new(Kind == MediaKind.Bilibili ? $"https://www.bilibili.com/video/{Id}" : $"https://store.steampowered.com/app/{Id}/");
}
public sealed record MediaDetails(MediaReference Reference, string Title, string Cover = "", string Creator = "",
    string CreatorAvatar = "", string Summary = "", string Error = "")
{
    public bool IsSuccess => Error.Length == 0;
}
public sealed record MediaPreviewSettings
{
    public bool DashboardEnabled { get; init; } = true;
    public MediaPreviewMode Mode { get; init; }
    public bool OverlayEnabled { get; init; } = true;
    public int OverlayMaxVisible { get; init; } = 2;
    public int Width { get; init; } = 420;
    public int CoverHeight { get; init; } = 96;
    public MediaPreviewSettings Normalize() => this with { Mode = Enum.IsDefined(Mode) ? Mode : MediaPreviewMode.Inline,
        OverlayMaxVisible = Math.Clamp(OverlayMaxVisible, 1, 4), Width = Math.Clamp(Width, 260, 800), CoverHeight = Math.Clamp(CoverHeight, 48, 160) };
}
public static class MediaDetector
{
    private static readonly Regex Pattern = new(@"(?<![A-Za-z0-9])(?<bv>[Bb][Vv]1[A-Za-z0-9]{9})(?![A-Za-z0-9])|(?:https?://store\.steampowered\.com/app/|steam\s*[: ]\s*|app/)(?<steam>[1-9][0-9]{0,9})(?![0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    public static IReadOnlyList<MediaReference> Detect(string content)
    {
        if (content.Length == 0) return Array.Empty<MediaReference>();
        var result = new List<MediaReference>(3);
        try
        {
            foreach (Match match in Pattern.Matches(content.Length > 16384 ? content[..16384] : content))
            {
                var bv = match.Groups["bv"].Value;
                var reference = bv.Length > 0 ? new MediaReference(MediaKind.Bilibili, "BV" + bv[2..]) : new(MediaKind.Steam, match.Groups["steam"].Value);
                if (!result.Contains(reference)) result.Add(reference);
                if (result.Count == 3) break;
            }
        }
        catch (RegexMatchTimeoutException) { }
        return result;
    }
}
