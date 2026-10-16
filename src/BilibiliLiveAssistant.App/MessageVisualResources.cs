using BilibiliLiveAssistant.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace BilibiliLiveAssistant.App;

// Shared only on the calling UI thread. Consumers assign these resources but
// never mutate them. Bounded caches also cover user-selected custom colors/fonts.
internal static class MessageVisualResources
{
    [ThreadStatic] private static Dictionary<uint, SolidColorBrush>? _brushes;
    [ThreadStatic] private static Dictionary<string, FontFamily>? _fonts;
    internal static SolidColorBrush Brush(string hex, double opacity)
    {
        if (!OverlayRegionSettings.IsColor(hex)) hex = "#FFFFFF";
        var argb = ((uint)(byte)Math.Clamp(opacity * 255, 0, 255) << 24) | Convert.ToUInt32(hex[1..], 16);
        var cache = _brushes ??= new();
        if (cache.TryGetValue(argb, out var brush)) return brush;
        if (cache.Count >= 256) cache.Clear();
        return cache[argb] = new(ColorHelper.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }
    internal static FontFamily Font(string name)
    {
        var cache = _fonts ??= new(StringComparer.Ordinal);
        if (cache.TryGetValue(name, out var font)) return font;
        if (cache.Count >= 32) cache.Clear();
        return cache[name] = new(name);
    }
}
