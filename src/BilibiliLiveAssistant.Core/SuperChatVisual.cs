using System.Globalization;

namespace BilibiliLiveAssistant.Core;

// Appearance tiers are independent of editable display durations.
public sealed record SuperChatPalette(int Tier, string Accent, string Fill, string Badge, string Text = "#FFFFFF");
public static class SuperChatVisual
{
    private static readonly SuperChatPalette[] Palettes = [
        new(0, "#A6B5CF", "#293447", "#4A5871"),
        new(1, "#77B8FF", "#193E66", "#235F9A"),
        new(2, "#6DE1DE", "#164C50", "#246C70"),
        new(3, "#FFD970", "#59421D", "#826018"),
        new(4, "#FFB278", "#64371E", "#975026"),
        new(5, "#FF9FCB", "#632D4A", "#963E70"),
        new(6, "#FF929B", "#682B35", "#A43D4C") ];
    public static long? AmountMilliYuan(LiveEvent value)
    {
        if (value.HistoryFields?.AmountMilliYuan is { } amount) return amount;
        if (value.Extra.GetValueOrDefault("price") is { } raw && decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
            NumberStyles.Number, CultureInfo.InvariantCulture, out var yuan) && yuan >= 0 && yuan <= long.MaxValue / 1000m) return (long)(yuan * 1000);
        return null;
    }
    public static SuperChatPalette For(LiveEvent value)
    {
        var amount = AmountMilliYuan(value);
        return Palettes[amount switch { null => 0, >= 2000000 => 6, >= 1000000 => 5, >= 500000 => 4, >= 100000 => 3, >= 50000 => 2, _ => 1 }];
    }
}
