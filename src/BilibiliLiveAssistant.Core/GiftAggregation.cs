using System.Globalization;

namespace BilibiliLiveAssistant.Core;

/// <summary>Shared identity and totals for gift history and overlay playback.</summary>
public static class GiftAggregation
{
    public static bool CanMerge(LiveEvent first, LiveEvent second)
    {
        if (first.Type != second.Type || first.Type is not (LiveEventType.Gift or LiveEventType.Guard)) return false;
        if (first.Uid > 0 && second.Uid > 0 && first.Uid != second.Uid) return false;
        if (first.Type == LiveEventType.Guard)
        {
            if (!string.Equals(Label(first), Label(second), StringComparison.Ordinal)) return false;
        }
        else
        {
            var id = Number(first, "gift_id", 0);
            var otherId = Number(second, "gift_id", 0);
            if (id > 0 || otherId > 0)
            {
                if (id != otherId) return false;
            }
            else if (!SameNonemptyText(first, second, "gift_name")) return false;
            if (!SameNonemptyText(first, second, "coin_type")) return false;
        }
        if (first.Uid > 0 && second.Uid == first.Uid) return true;
        // Masked names and UID 0 are not user identities.
        return SameNonemptyText(first, second, "transaction_id");
    }

    public static LiveEvent Combine(LiveEvent first, LiveEvent second)
    {
        var count = Add(Number(first, "num", 1), Number(second, "num", 1));
        var coins = Add(Number(first, "total_coin", 0), Number(second, "total_coin", 0));
        var label = Label(first);
        var metadata = new Dictionary<string, object?>(first.Extra)
        {
            ["num"] = count, ["total_coin"] = coins, ["gift_label"] = label,
        };
        var money = Text(first, "coin_type") == "gold" && coins > 0
            ? $" · ¥{(coins / 1000m).ToString("0.###", CultureInfo.InvariantCulture)}" : "";
        var fields = first.HistoryFields;
        var other = second.HistoryFields;
        return first with { Content = $"{label}×{count}{money}", Metadata = metadata,
            HistoryFields = fields is null || other is null ? null : fields with {
                Quantity = fields.Quantity is { } q && other.Quantity is { } oq ? Add(q, oq) : null,
                TotalCoin = fields.TotalCoin is { } c && other.TotalCoin is { } oc ? Add(c, oc) : null,
                AmountMilliYuan = fields.AmountMilliYuan is { } a && other.AmountMilliYuan is { } oa ? Add(a, oa) : null },
            Uid = first.Uid > 0 ? first.Uid : second.Uid, Appearance = first.Appearance ?? second.Appearance };
    }

    private static string Label(LiveEvent value)
    {
        var saved = Text(value, "gift_label");
        if (saved.Length > 0) return saved;
        var end = value.Content.LastIndexOf('×');
        return end >= 0 ? value.Content[..end].TrimEnd() : value.Content.TrimEnd();
    }
    private static bool SameNonemptyText(LiveEvent first, LiveEvent second, string key)
    {
        var text = Text(first, key);
        return !string.IsNullOrWhiteSpace(text) && string.Equals(text, Text(second, key), StringComparison.Ordinal);
    }
    private static string Text(LiveEvent value, string key) => value.Extra.TryGetValue(key, out var text) && text is string s ? s : "";
    private static long Number(LiveEvent value, string key, long fallback) => value.Extra.TryGetValue(key, out var number)
        && long.TryParse(Convert.ToString(number, CultureInfo.InvariantCulture), out var count) ? Math.Max(fallback, count) : fallback;
    private static long Add(long first, long second) => first + Math.Min(long.MaxValue - first, second);
}
