using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using BilibiliLiveAssistant.Core;
using BilibiliLiveAssistant.Infrastructure;

namespace BilibiliLiveAssistant.App;

public sealed partial class LiveAssistantViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISettingsStore _store;
    private readonly ILiveRoomClient _client;
    private readonly ILiveInfoService _info;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _eventTimer;
    private readonly LiveEventBuffer _pendingEvents = new(256);
    private int _flushScheduled;
    private long _receivedEvents;
    private long _overlayDropped;
    private int _overlayMediaCount;
    private volatile bool _disposed;
    private AppSettings _settings;

    private string _status = "未连接";
    private bool _isConnected;
    private string _onlineText = "--";
    private string _rankText = "暂无榜单";
    private string _roomSummary = "房间状态暂无数据";
    private string _errorText = string.Empty;
    private string _popularity = "", _watched = "";

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }
    public bool IsListening => _client.IsRunning;
    public string OnlineText { get => _onlineText; private set => SetProperty(ref _onlineText, value); }
    public string RankText { get => _rankText; private set => SetProperty(ref _rankText, value); }
    public string RoomSummary { get => _roomSummary; private set => SetProperty(ref _roomSummary, value); }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }

    public LiveEventHistory Events { get; } = new();
    public DashboardHistory Dashboard { get; } = new();
    public DanmakuCardBoard CardBoard { get; } = new();
    internal MediaDetailsService Media { get; } = new();
    internal BiliImageCache Images { get; } = new();
    public event Action<IReadOnlyList<LiveEvent>>? EventBatchReceived;
    public event Action? RoomHistoryReset;
    internal LiveEvent? LatestOnline { get; private set; }
    internal LiveEvent? LatestRank { get; private set; }
    internal LiveRoomProfile? RoomProfile { get; private set; }
    internal List<PopularitySample> PopularityHistory { get; } = new(240);
    internal CookieOptions DashboardCookies => ConnectionCookies;
    public long ReceivedEventCount => Interlocked.Read(ref _receivedEvents);
    public long BufferedDroppedCount => _pendingEvents.DroppedCount;
    public long OverlayDroppedCount => Interlocked.Read(ref _overlayDropped);
    internal double EventBatchIntervalSeconds => _eventTimer.Interval.TotalSeconds;
    internal bool CanShowOverlayMedia => _settings.MediaPreview.OverlayEnabled && _overlayMediaCount < _settings.MediaPreview.OverlayMaxVisible;
    internal bool AcquireOverlayMedia() { if (!CanShowOverlayMedia) return false; _overlayMediaCount++; return true; }
    internal void ReleaseOverlayMedia() => _overlayMediaCount = Math.Max(0, _overlayMediaCount - 1);
    public void RecordOverlayDropped(int count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _overlayDropped, count);
        OnPropertyChanged(nameof(OverlayDroppedCount));
    }
    public AppSettings Settings => _settings;
    public int RoomId => _settings.RoomId;
    public bool IsAnonymousReading => _settings.AnonymousReading || string.IsNullOrWhiteSpace(_settings.Cookies.Sessdata);
    public string ConnectionModeText => IsAnonymousReading ? "匿名读取 · 无需登录" : "使用账号 Cookie 读取";
    private CookieOptions ConnectionCookies => EffectiveCookies(_settings);
    private static CookieOptions EffectiveCookies(AppSettings settings) => settings.AnonymousReading ? new CookieOptions()
        : string.IsNullOrWhiteSpace(settings.Cookies.Sessdata) ? new CookieOptions(Buvid3: settings.Cookies.Buvid3) : settings.Cookies;

    public LiveAssistantViewModel(AppSettings settings, ISettingsStore store, ILiveRoomClient client, ILiveInfoService info)
    {
        _settings = settings with { Dashboard = (settings.Dashboard ?? new()).Normalize(),
            MediaPreview = (settings.MediaPreview ?? new()).Normalize(), Cards = (settings.Cards ?? new()).Normalize() };
        Dashboard.Cleared += CardBoard.Clear;
        _store = store;
        _client = client;
        _info = info;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _eventTimer = _dispatcher.CreateTimer();
        ApplyEventInterval();
        _eventTimer.Tick += FlushEvents;
        _client.EventReceived += OnEvent;
        _client.StatusChanged += OnStatus;
        _info.EventReceived += OnEvent;
    }

    public async Task StartAsync(int roomId)
    {
        if (roomId <= 0) { ErrorText = "请输入有效的直播间ID"; return; }
        if (_client.IsRunning) return;
        PopularityHistory.Clear();
        ErrorText = string.Empty;
        if (_settings.RoomId != roomId) ResetRoomHistory();
        _settings = _settings with { RoomId = roomId };
        await _store.SaveAsync(_settings);
        try
        {
            await _client.StartAsync(roomId, ConnectionCookies);
            OnPropertyChanged(nameof(IsListening));
            await _info.StartAsync(roomId, ConnectionCookies, _settings.PollIntervalSeconds);
        }
        catch (Exception ex)
        {
            await StopAsync();
            ErrorText = ex.Message;
            Status = "连接失败";
        }
    }

    public async Task StopAsync()
    {
        await _info.StopAsync();
        await _client.StopAsync();
        OnPropertyChanged(nameof(IsListening));
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var previous = _settings;
        var wasRunning = _client.IsRunning;
        if (wasRunning && previous.RoomId != settings.RoomId) await StopAsync();
        settings = settings with { Dashboard = (settings.Dashboard ?? new()).Normalize(),
            MediaPreview = (settings.MediaPreview ?? new()).Normalize(), Cards = (settings.Cards ?? new()).Normalize() };
        _settings = settings;
        if (previous.RoomId != settings.RoomId) ResetRoomHistory();
        Dashboard.Trim(settings.Dashboard);
        ApplyEventInterval();
        await _store.SaveAsync(settings);
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(RoomId));
        OnPropertyChanged(nameof(IsAnonymousReading));
        OnPropertyChanged(nameof(ConnectionModeText));

        if (!wasRunning) return;

        // Cookie 或房间变更后必须重建 WebSocket，避免继续使用旧的认证信息。
        if (previous.RoomId != settings.RoomId || EffectiveCookies(previous) != ConnectionCookies)
        {
            await StopAsync();
            if (settings.RoomId > 0)
                await StartAsync(settings.RoomId);
            return;
        }

        if (previous.PollIntervalSeconds != settings.PollIntervalSeconds)
        {
            await _info.StopAsync();
            await _info.StartAsync(settings.RoomId, ConnectionCookies, settings.PollIntervalSeconds);
        }
    }

    public void SendTestDanmaku(int? sequence = null)
    {
        OnEvent(this, new LiveEvent(
            LiveEventType.Danmaku,
            "测试用户",
            sequence is null ? "这是一条弹幕测试消息" : $"高流量模拟弹幕 #{sequence}",
            _settings.Display.DanmakuColor));
    }

    private void ApplyEventInterval() => _eventTimer.Interval = TimeSpan.FromMilliseconds(
        Enum.GetValues<OverlayRegionKind>().Where(kind => OverlayRegions.Enabled(_settings.Display, kind))
            .Min(kind => OverlayRegions.Get(_settings.Display, kind).UpdateIntervalMs));

    public void ReportSettingsError(string message) => ErrorText = message;

    internal LiveRankMember[] GetRankMembers(LiveEvent rank, int count)
    {
        if (rank.Extra.TryGetValue("rank_members", out var data) && data is IReadOnlyList<LiveRankMember> members)
        {
            var isSample = rank.Extra.TryGetValue("sample", out var sample) && sample is true;
            return members.Take(Math.Clamp(count, 1, 10)).Select(member =>
            {
                if (isSample || member.Uid <= 0) return member;
                // Reuse bounded room history; no new profile requests or persistent user cache.
                var cutoff = DateTimeOffset.Now.AddMinutes(-3);
                var recent = Events.FirstOrDefault(value => value.Uid == member.Uid && value.Appearance is not null
                    && value.OccurredAt >= cutoff && !(value.Extra.TryGetValue("sample", out var example) && example is true));
                if (recent?.Appearance is not { } observed) return member;
                var current = member.Appearance;
                var avatar = current.AvatarUrl;
                if (avatar.Length == 0)
                    avatar = Events.FirstOrDefault(value => value.Uid == member.Uid && value.Appearance?.AvatarUrl is { Length: > 0 }
                        && value.OccurredAt >= cutoff && !(value.Extra.TryGetValue("sample", out var example) && example is true))?.Appearance?.AvatarUrl ?? "";
                return member with { Appearance = current with
                {
                    AvatarUrl = avatar,
                    AvatarFrameUrl = current.AvatarFrameUrl.Length > 0 ? current.AvatarFrameUrl : observed.AvatarFrameUrl,
                    MedalName = member.HasMedalData ? current.MedalName : observed.MedalName,
                    MedalLevel = member.HasMedalData ? current.MedalLevel : observed.MedalLevel,
                    MedalColor = member.HasMedalData ? current.MedalColor : observed.MedalColor,
                    GuardLevel = member.HasGuardData ? current.GuardLevel : observed.GuardLevel,
                } };
            }).ToArray();
        }
        if (rank.Content == "暂无榜单") return Array.Empty<LiveRankMember>();
        var names = rank.Extra.TryGetValue("rank_names", out var rows) && rows is string[] supplied ? supplied
            : rank.Content.Replace("高能榜:", "", StringComparison.Ordinal).Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return names.Take(Math.Clamp(count, 1, 10)).Select((name, index) =>
        {
            var dot = name.IndexOf('.');
            if (dot > 0 && int.TryParse(name.AsSpan(0, dot), out _)) name = name[(dot + 1)..];
            return new LiveRankMember(index + 1, 0, name, new());
        }).ToArray();
    }

    public void SendDisplayExamples()
    {
        void Example(LiveEvent value) => OnEvent(this, value with
            { Metadata = new Dictionary<string, object?>(value.Extra) { ["sample"] = true } });
        var small = new LiveEmote("[热]", "https://i0.hdslb.com/bfs/live/6df760280b17a6cbac8c1874d357298f982ba4cf.png", 20, 20);
        var appearance = new LiveMessageAppearance { MedalName = "示例牌", MedalLevel = 21, MedalColor = "#4388BC", GuardLevel = 3, IsAdmin = true, Emotes = new[] { small } };
        Example(new LiveEvent(LiveEventType.Danmaku, "图文示例用户", "这是一条带 B站图片表情的弹幕 [热]，也支持普通 emoji 😊", "#FFFFFF", 5, Appearance: appearance));
        Example(new LiveEvent(LiveEventType.Danmaku, "大表情示例", "[B站图片表情]", "#FFFFFF", 6,
            Appearance: new LiveMessageAppearance { MedalName = "示例牌", MedalLevel = 30,
                FullEmote = new("[B站图片表情]", "https://i0.hdslb.com/bfs/live/a98e35996545509188fe4d24bd1a56518ea5af48.png", 183, 60, true) }));
        Example(new LiveEvent(LiveEventType.Gift, "示例用户", "赠送 小心心×1", "#FF69B4", 1,
            Metadata: new Dictionary<string, object?> { ["gift_id"] = 1L, ["num"] = 1L, ["total_coin"] = 0L, ["coin_type"] = "silver" }));
        Example(new LiveEvent(LiveEventType.Gift, "示例用户", "赠送 小心心×2", "#FF69B4", 1,
            Metadata: new Dictionary<string, object?> { ["gift_id"] = 1L, ["num"] = 2L, ["total_coin"] = 0L, ["coin_type"] = "silver" }));
        Example(new LiveEvent(LiveEventType.Guard, "示例舰长", "开通了舰长！", "#FF69B4", 2));
        Example(new LiveEvent(LiveEventType.SuperChat, "示例留言", "¥30 这是一条醒目留言示例，可以单独调整滚动速度、字号和表情尺寸。", "#FFD700", 3));
        Example(new LiveEvent(LiveEventType.SuperChat, "长留言示例", "¥50 " + string.Concat(Enumerable.Repeat("长 SC 会连续分段滚动，同一条的各段不会插入其他留言 [热] 😊。", 8)), "#FFD700", 10,
            Appearance: appearance));
        var entryAvatar = "https://i0.hdslb.com/bfs/face/109b787c5f113c4a3c7415bb9bf6b82bc33bc4e2.jpg";
        Example(new LiveEvent(LiveEventType.Enter, "普通进房示例", "进入了直播间", "#4CAF50", 4,
            Appearance: new LiveMessageAppearance { AvatarUrl = entryAvatar }));
        Example(new LiveEvent(LiveEventType.Enter, "等级进场示例", "来了", "#4CAF50", 9,
            Appearance: new LiveMessageAppearance { AvatarUrl = entryAvatar, WealthLevel = 15, EntryEffect = new() }));
        Example(new LiveEvent(LiveEventType.Enter, "回归观众示例", "进入了直播间", "#4CAF50", 7,
            Appearance: new LiveMessageAppearance { AvatarUrl = entryAvatar, InteractionLabel = "回归", MedalName = "示例牌", MedalLevel = 33, GuardLevel = 3 }));
        Example(new LiveEvent(LiveEventType.Enter, "专属进场示例", "来了", "#4CAF50", 8,
            Appearance: new LiveMessageAppearance { AvatarUrl = entryAvatar, MedalName = "示例牌", MedalLevel = 33, GuardLevel = 3,
                EntryEffect = new(HighlightColor: "#FFE96D") }));
        var sample = new Dictionary<string, object?> { ["sample"] = true };
        Example(new LiveEvent(LiveEventType.Online, "示例系统", "示例人气：1234 · 看过：5678", "#2196F3", Metadata: sample));
        Example(new LiveEvent(LiveEventType.Rank, "示例系统", "高能榜: 1.示例观众甲 | 2.示例观众乙 | 3.示例观众丙", "#9C27B0",
            Metadata: new Dictionary<string, object?>(sample)
            {
                ["rank_names"] = Enumerable.Range(1, 10).Select(i => $"{i}.示例观众{i}").ToArray(),
                ["rank_members"] = Enumerable.Range(1, 10).Select(i => new LiveRankMember(i, 10000 + i, $"示例观众{i}",
                    new() { AvatarUrl = entryAvatar, MedalName = "示例牌", MedalLevel = 20 + i, MedalColor = "#4388BC",
                        GuardLevel = i switch { 1 => 3, 2 => 2, 3 => 1, _ => 0 } })).ToArray(),
            }));
    }

    private void OnStatus(object? sender, string value)
    {
        void Update()
        {
            if (_disposed) return;
            Status = value;
            IsConnected = value == "已连接";
            OnPropertyChanged(nameof(IsListening));
        }
        if (_dispatcher.HasThreadAccess) Update();
        else _dispatcher.TryEnqueue(Update);
    }

    private void OnEvent(object? sender, LiveEvent value)
    {
        if (_disposed) return;
        if (value.Type is LiveEventType.Danmaku or LiveEventType.SuperChat && value.MediaReferences.Count == 0)
            value = value with { MediaReferences = MediaDetector.Detect(value.Content) };
        Interlocked.Increment(ref _receivedEvents);
        _pendingEvents.Enqueue(value);
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        // Keep one scheduled wake for the entire burst, not one dispatcher work
        // item per message. The timer stops as soon as the buffer is empty.
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0) return;
        if (!_dispatcher.TryEnqueue(() => { if (!_disposed) _eventTimer.Start(); }))
            Interlocked.Exchange(ref _flushScheduled, 0);
    }

    private void FlushEvents(DispatcherQueueTimer sender, object args)
    {
        var batch = _pendingEvents.Drain();
        if (batch.Length > 0)
        {
            foreach (var value in batch)
            {
                // Local overlay examples must not replace real room metrics in the dashboard.
                if (value.Extra.TryGetValue("sample", out var sample) && sample is true) continue;
                if (value.Type == LiveEventType.Online)
                {
                    if (value.Extra.TryGetValue("popularity", out var popularity) && popularity is string p) _popularity = p;
                    if (value.Extra.TryGetValue("watched", out var watched) && watched is string { Length: > 0 } w) _watched = w;
                    var metrics = new List<string>();
                    if (_popularity.Length > 0) metrics.Add("人气: " + _popularity);
                    if (_watched.Length > 0) metrics.Add("看过: " + _watched.Replace("人看过", "", StringComparison.Ordinal));
                    OnlineText = metrics.Count > 0 ? string.Join(" · ", metrics) : value.Content;
                    LatestOnline = value with { Content = OnlineText,
                        Metadata = new Dictionary<string, object?>(value.Extra) { ["popularity"] = _popularity, ["watched"] = _watched } };
                    if (value.Extra.TryGetValue("room_summary", out var summary) && summary is string text) RoomSummary = text;
                    if (value.Extra.TryGetValue("room_profile", out var profile) && profile is LiveRoomProfile suppliedProfile)
                    {
                        RoomProfile = suppliedProfile with { EnergyMembers = RoomProfile?.EnergyMembers };
                        OnPropertyChanged(nameof(RoomProfile));
                    }
                    if (IsListening && long.TryParse(_popularity, out var count) && count >= 0
                        && (PopularityHistory.Count == 0 || value.OccurredAt - PopularityHistory[^1].Time >= TimeSpan.FromSeconds(15)))
                    {
                        if (PopularityHistory.Count >= 240) PopularityHistory.RemoveAt(0);
                        PopularityHistory.Add(new(value.OccurredAt, count));
                    }
                }
                if (value.Type == LiveEventType.Rank)
                {
                    LatestRank = value; RankText = value.Content;
                    if (RoomProfile is { } profile && value.Extra.TryGetValue("rank_total", out var total) && total is long number)
                    { RoomProfile = profile with { EnergyMembers = number }; OnPropertyChanged(nameof(RoomProfile)); }
                }
            }
            Dashboard.AddBatch(batch, _settings.Dashboard, OverlayRegions.Get(_settings.Display, OverlayRegionKind.Gift).MergeSeconds);
            CardBoard.AddBatch(batch, _settings.Cards);
            Events.PrependBatch(batch);
            EventBatchReceived?.Invoke(batch);
            OnPropertyChanged(nameof(ReceivedEventCount));
        }
        if (_pendingEvents.Count != 0) return;
        _eventTimer.Stop();
        Interlocked.Exchange(ref _flushScheduled, 0);
        // A producer can enqueue between the empty check and flag reset.
        if (_pendingEvents.Count > 0) ScheduleFlush();
    }

    private void ResetRoomHistory()
    {
        _pendingEvents.Clear(); Events.Clear(); Dashboard.Clear();
        LatestOnline = null; LatestRank = null;
        RoomProfile = null; PopularityHistory.Clear(); OnPropertyChanged(nameof(RoomProfile));
        _popularity = ""; _watched = "";
        RoomHistoryReset?.Invoke();
        OnlineText = "--"; RankText = "暂无榜单"; RoomSummary = "房间状态暂无数据";
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _eventTimer.Stop();
        _eventTimer.Tick -= FlushEvents;
        _client.EventReceived -= OnEvent;
        _client.StatusChanged -= OnStatus;
        _info.EventReceived -= OnEvent;
        _pendingEvents.Clear();
        Images.Dispose();
        Media.Dispose(); Dashboard.Cleared -= CardBoard.Clear;
        await StopAsync();
        if (_client is IDisposable disposable) disposable.Dispose();
        if (_info is IDisposable info) info.Dispose();
    }
}
