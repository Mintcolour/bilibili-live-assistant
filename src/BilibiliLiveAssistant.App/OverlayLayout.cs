using System.Drawing;
using BilibiliLiveAssistant.Core;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

internal static class OverlayLayout
{
    internal static Rectangle Bounds(DisplaySettings display, OverlayRegionKind kind, OverlayRegionSettings region)
    {
        var work = Forms.Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var scrollHeight = Math.Clamp((int)(work.Height * display.WindowHeightRatio), 80, Math.Max(80, work.Height - 220));
        var cardY = work.Y + scrollHeight + 16; var cardHeight = Math.Max(80, work.Bottom - 120 - cardY);
        var defaults = kind switch
        {
            OverlayRegionKind.Danmaku => new Rectangle(work.X, work.Y, work.Width, scrollHeight),
            OverlayRegionKind.Gift => new Rectangle(work.Right - 380, cardY, 360, Math.Min(360, cardHeight)),
            OverlayRegionKind.SuperChat => new Rectangle(work.X + 20, cardY, 440, Math.Min(520, cardHeight)),
            OverlayRegionKind.Interaction => new Rectangle(work.X + 20, work.Bottom - 200, 420, 180),
            _ => region.Mode == OverlayDisplayMode.Panel ? new Rectangle(work.Right - 440, work.Bottom - 284, 420, 264)
                : new Rectangle(work.Right - 440, work.Bottom - 150, 420, 130),
        };
        var bounds = new Rectangle(region.X ?? defaults.X, region.Y ?? defaults.Y,
            region.Width > 0 ? region.Width : defaults.Width, region.Height > 0 ? region.Height : defaults.Height);
        var screen = Forms.Screen.FromRectangle(bounds).WorkingArea;
        bounds.Width = Math.Clamp(bounds.Width, 160, Math.Max(160, screen.Width));
        bounds.Height = Math.Clamp(bounds.Height, 80, Math.Max(80, screen.Height));
        bounds.X = Math.Clamp(bounds.X, screen.Left, Math.Max(screen.Left, screen.Right - bounds.Width));
        bounds.Y = Math.Clamp(bounds.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - bounds.Height));
        return bounds;
    }
}
