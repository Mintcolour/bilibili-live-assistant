namespace BilibiliLiveAssistant.Core;

public enum GiftCategory { All, Paid, Free, Guard }
public enum MessageSort { Newest, Oldest, AmountDescending, AmountAscending, QuantityDescending }

public sealed record TradeFilter
{
    public static TradeFilter Default { get; } = new();
    public bool OnlyUnread { get; init; }
    public GiftCategory Category { get; init; }
    public string GiftName { get; init; } = "";
    public long? MinimumMilliYuan { get; init; }
    public long? MaximumMilliYuan { get; init; }
    public MessageSort Sort { get; init; }
    public TradeFilter Normalize() => this with
    {
        Category = Enum.IsDefined(Category) ? Category : GiftCategory.All,
        Sort = Enum.IsDefined(Sort) ? Sort : MessageSort.Newest,
        GiftName = (GiftName ?? "").Trim()[..Math.Min((GiftName ?? "").Trim().Length, 128)],
        MinimumMilliYuan = MinimumMilliYuan is >= 0 ? MinimumMilliYuan : null,
        MaximumMilliYuan = MaximumMilliYuan is >= 0 ? MaximumMilliYuan : null,
    };
    public bool IsDefault => this == Default;
    public bool Matches(LiveEvent value)
    {
        var fields = value.HistoryFields;
        if (value.Type is LiveEventType.Gift or LiveEventType.Guard)
        {
            if (Category == GiftCategory.Paid && (value.Type != LiveEventType.Gift || fields?.CoinType != "gold")) return false;
            if (Category == GiftCategory.Free && (value.Type != LiveEventType.Gift || fields?.CoinType != "silver")) return false;
            if (Category == GiftCategory.Guard && value.Type != LiveEventType.Guard) return false;
            if (GiftName.Length > 0 && !string.Equals(fields?.GiftName ?? "", GiftName, StringComparison.Ordinal)) return false;
        }
        if (value.Type == LiveEventType.SuperChat && (MinimumMilliYuan is not null || MaximumMilliYuan is not null))
        {
            if (fields?.AmountMilliYuan is not { } amount) return false;
            if (MinimumMilliYuan is { } min && amount < min || MaximumMilliYuan is { } max && amount > max) return false;
        }
        return true;
    }
    public int Compare(DashboardRecord a, DashboardRecord b)
    {
        var left = a.Event.HistoryFields; var right = b.Event.HistoryFields;
        var comparison = Sort switch
        {
            MessageSort.AmountDescending => CompareNullable(left?.AmountMilliYuan, right?.AmountMilliYuan, true),
            MessageSort.AmountAscending => CompareNullable(left?.AmountMilliYuan, right?.AmountMilliYuan, false),
            MessageSort.QuantityDescending => CompareNullable(left?.Quantity, right?.Quantity, true),
            _ => 0,
        };
        if (comparison != 0) return comparison;
        comparison = Sort == MessageSort.Oldest ? a.CreatedAt.CompareTo(b.CreatedAt) : b.CreatedAt.CompareTo(a.CreatedAt);
        return comparison != 0 ? comparison : a.Id.CompareTo(b.Id);
    }
    private static int CompareNullable(long? left, long? right, bool descending)
    {
        if (left is null) return right is null ? 0 : 1;
        if (right is null) return -1;
        return descending ? right.Value.CompareTo(left.Value) : left.Value.CompareTo(right.Value);
    }
}

public sealed record TradeViewSettings
{
    public TradeFilter Gifts { get; init; } = new();
    public TradeFilter SuperChats { get; init; } = new();
    public TradeFilter History { get; init; } = new();
    public LiveEventType? HistoryType { get; init; }
    public TradeViewSettings Normalize() => this with
    { Gifts = (Gifts ?? new()).Normalize(), SuperChats = (SuperChats ?? new()).Normalize(), History = (History ?? new()).Normalize(),
        HistoryType = HistoryType is { } type && Enum.IsDefined(type) ? type : null };
}
