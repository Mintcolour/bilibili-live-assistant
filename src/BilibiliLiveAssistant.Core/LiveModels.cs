namespace BilibiliLiveAssistant.Core;

public enum LiveEventType
{
    Danmaku,
    Enter,
    Gift,
    SuperChat,
    Guard,
    Online,
    Rank,
}

public sealed record LiveEvent(
    LiveEventType Type,
    string Username,
    string Content,
    string Color,
    long Uid = 0,
    DateTimeOffset? Timestamp = null,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    LiveMessageAppearance? Appearance = null)
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyMetadata =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
    public DateTimeOffset OccurredAt { get; } = Timestamp ?? DateTimeOffset.Now;
    public IReadOnlyDictionary<string, object?> Extra => Metadata ?? EmptyMetadata;
    public IReadOnlyList<MediaReference> MediaReferences { get; init; } = Array.Empty<MediaReference>();
    public HistoryEventFields? HistoryFields { get; init; }
}

public sealed record CookieOptions(string Buvid3 = "", string Sessdata = "", string BiliJct = "")
{
    public override string ToString() => "CookieOptions [已隐藏]";
}

public sealed record LiveSnapshot(int Online, string WatchedText, string RankText);

public sealed record DisplaySettings
{
    public int FontSize { get; init; } = 28;
    public int ScrollSpeed { get; init; } = 120;
    // 0 follows the display containing the ordinary danmaku region (up to 240 Hz).
    public int AnimationFrameRate { get; init; } = 60;
    public double Opacity { get; init; } = 0.85;
    public int MaxTracks { get; init; } = 8;
    public int TrackHeight { get; init; } = 36;
    public double WindowHeightRatio { get; init; } = 0.35;
    public bool ShowEnter { get; init; } = true;
    public bool ShowGift { get; init; } = true;
    public bool ShowSuperChat { get; init; } = true;
    public bool ShowOnline { get; init; } = true;
    public string DanmakuColor { get; init; } = "#FFFFFF";
    public string EnterColor { get; init; } = "#4CAF50";
    public string GiftColor { get; init; } = "#FF69B4";
    public string SuperChatColor { get; init; } = "#FFD700";
    public string OnlineColor { get; init; } = "#2196F3";
    public bool LayoutLocked { get; init; } = true;
    public OverlayRegionSettings? DanmakuRegion { get; init; }
    public OverlayRegionSettings? GiftRegion { get; init; }
    public OverlayRegionSettings? SuperChatRegion { get; init; }
    public OverlayRegionSettings? InteractionRegion { get; init; }
    public OverlayRegionSettings? StatusRegion { get; init; }
}

public sealed record AppSettings
{
    public HotPhraseSettings HotPhrases { get; init; } = new();
    public CompactOverlaySettings Compact { get; init; } = new();
    public OverlayPriceDurations PriceDurations { get; init; } = new();
    public int SchemaVersion { get; init; } = 1;
    public int RoomId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public CookieOptions Cookies { get; init; } = new();
    public BrowserSyncSettings BrowserSync { get; init; } = new();
    public bool AnonymousReading { get; init; }
    public DisplaySettings Display { get; init; } = new();
    public DashboardSettings Dashboard { get; init; } = new();
    public MediaPreviewSettings MediaPreview { get; init; } = new();
    public DanmakuCardSettings Cards { get; init; } = new();
    public HistorySettings History { get; init; } = new();
    public TradeViewSettings TradeViews { get; init; } = new();
    public int PollIntervalSeconds { get; init; } = 30;
}

public interface ILiveRoomClient
{
    event EventHandler<LiveEvent>? EventReceived;
    event EventHandler<string>? StatusChanged;
    bool IsRunning { get; }
    Task StartAsync(int roomId, CookieOptions cookies, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface ILiveInfoService
{
    void ConfigureAnalysis(HistoryAnalysisSettings settings, bool enabled);
    void SetConnectionAvailable(bool available);
    event EventHandler<LiveEvent>? EventReceived;
    Task StartAsync(int roomId, CookieOptions cookies, int intervalSeconds, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface ISettingsStore
{
    string Path { get; }
    AppSettings Load();
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
