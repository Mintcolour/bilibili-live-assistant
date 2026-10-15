namespace BilibiliLiveAssistant.Core;

public sealed record HistorySettings
{
    public bool Enabled { get; init; } = true;
    public string Directory { get; init; } = "";
    public int CapacityWarningGiB { get; init; } = 10;
    public HistorySettings Normalize() => this with { Directory = string.IsNullOrWhiteSpace(Directory) ? "" : Path.GetFullPath(Directory.Trim()), CapacityWarningGiB = Math.Clamp(CapacityWarningGiB, 1, 4096) };
    public string EffectiveDirectory => string.IsNullOrWhiteSpace(Directory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BilibiliLiveAssistant", "history")
        : Path.GetFullPath(Directory);
}

// Only explicitly parsed source fields are persisted. Never serialize arbitrary event metadata.
public sealed record HistoryEventFields(DateTimeOffset? UpstreamTime = null, string? UniqueId = null,
    long? GiftId = null, string? GiftName = null, long? Quantity = null, string? CoinType = null,
    long? UnitPrice = null, long? TotalCoin = null, long? AmountMilliYuan = null, int? GuardLevel = null,
    int? InteractionKind = null, string? TransactionId = null, string? RawBody = null);
public sealed record HistoryContext(string Id, int RoomId, DateTimeOffset StartedAt);
public sealed record HistoryEnvelope(HistoryContext Context, DateTimeOffset ReceivedAt, string LocalDay,
    LiveEvent Event, HistoryEventFields Fields);
public sealed record HistoryAnchor(long? Uid, int RoomId, string Name, string AvatarUrl)
{
    public string Label => Uid is > 0 ? $"{Name} · {Uid}" : $"待识别房间 · {RoomId}";
    public override string ToString() => Label;
}
public sealed record HistorySession(string Id, int RoomId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string Status, string FirstDay, string LastDay)
{
    public override string ToString() => $"{StartedAt.ToLocalTime():MM-dd HH:mm} · {Status switch { "active" => "记录中", "stopped" => "已停止", _ => "中断" }}";
}
public sealed record HistoryFilter(HistoryAnchor Anchor, string FromDay, string ToDay, string? SessionId = null,
    LiveEventType? Type = null, long? SenderUid = null, string Nickname = "", string Keyword = "", long? BeforeId = null,
    DateTimeOffset? BeforeReceivedAt = null);
public sealed record HistoryDetails(LiveRoomProfile? Profile, long Snapshots, DateTimeOffset? SnapshotAt,
    long? Popularity, long? Watched, IReadOnlyList<LiveRankMember>? Rank, long? RankTotal);
public sealed record HistoryRow(long Id, DateTimeOffset ReceivedAt, DateTimeOffset? UpstreamAt, string LocalDay,
    string SessionId, LiveEvent Event, HistoryEventFields Fields);
public sealed record HistorySummary(long Events, long Danmaku, long Interactions, long Gifts, long Guards, long SuperChats,
    long PaidGiftMilliYuan, long FreeGiftQuantity, long SuperChatMilliYuan, long UnknownPrices, long Missing, bool Incomplete, long UnassignedMissing = 0, long InterruptedSessions = 0);
public sealed record HistoryGap(string? SessionId, int RoomId, LiveEventType Type, long Count,
    DateTimeOffset FirstAt, DateTimeOffset LastAt, string Reason)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}
public sealed record HistoryStatus(string Message, long Dropped, long Bytes, int Pending, bool Faulted, bool CapacityWarning);
public interface ILocalHistory : IAsyncDisposable
{
    event Action<HistoryStatus>? StatusChanged;
    event Action? Committed;
    HistoryStatus Status { get; }
    string DatabasePath { get; }
    HistoryContext Begin(int roomId);
    void End(HistoryContext context);
    void Capture(HistoryContext context, LiveEvent value, DateTimeOffset receivedAt);
    Task<IReadOnlyList<HistoryAnchor>> AnchorsAsync(CancellationToken token, string search = "", int limit = 2000);
    Task<IReadOnlyList<HistorySession>> SessionsAsync(HistoryFilter filter, CancellationToken token);
    Task<IReadOnlyList<HistoryRow>> QueryAsync(HistoryFilter filter, CancellationToken token);
    Task<HistorySummary> SummaryAsync(HistoryFilter filter, CancellationToken token);
    Task<HistoryDetails> DetailsAsync(HistoryFilter filter, CancellationToken token);
    Task<IReadOnlyList<HistoryGap>> GapsAsync(HistoryFilter filter, CancellationToken token);
    Task<long> CountAsync(HistoryFilter filter, CancellationToken token);
    Task DeleteAsync(HistoryFilter filter, CancellationToken token);
    Task CompactAsync(CancellationToken token);
    Task MoveAsync(string directory, CancellationToken token);
    void Configure(HistorySettings settings);
    Task<bool> FlushAsync(TimeSpan timeout);
}
