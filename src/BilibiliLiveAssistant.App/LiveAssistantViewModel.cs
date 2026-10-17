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
    private readonly DispatcherQueueTimer _authTimer;
    private readonly DispatcherQueueTimer _hotTimer;
    private readonly HotPhraseWindow _hotWindow = new();
    internal IReadOnlyList<HotPhrase> HotPhrases { get; private set; } = Array.Empty<HotPhrase>();
    internal event Action? HotPhrasesChanged;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private CancellationTokenSource? _connectionRun;
    private CookieOptions _runtimeCookies = new(), _startedCookies = new();
    private string _accountName = "", _accountReason = "无需登录";
    private bool _monitorRequested, _authApplying;
    private long _credentialsRevision;
    internal BrowserAccountController AccountSync { get; }
    internal event Action? AuthenticationChanged;
    private readonly LiveEventBuffer _pendingEvents = new(256);
    private readonly object _roomEvents = new();
    private int _resourceResets, _lastResetReason;
    private bool _roomOffline, _liveEnded;
    private int _flushScheduled;
    private long _receivedEvents;
    private long _overlayDropped;
    private int _overlayMediaCount;
    private volatile bool _disposed;
    private AppSettings _settings;
    internal ILocalHistory History { get; }
    internal PerformanceLog Diagnostics { get; }
    internal CompactOverlayController CompactOverlay { get; }
    internal Func<object>? WindowDiagnostics { get; set; }
    private HistoryContext? _historyContext;
    private EventHandler<LiveEvent>? _networkHandler;
    private int _historyNoticeScheduled;
    private int _streamAvailable;
    private string _historyConfigurationWarning = "";
    public bool IsMonitoring => _monitorRequested;
    private bool _overlayInteractive;
    public bool OverlayInteractive => _overlayInteractive;
    public void ToggleOverlayInteraction() { _overlayInteractive = !_overlayInteractive; OnPropertyChanged(nameof(OverlayInteractive)); }
    internal void EndOverlayInteraction() { if (!_overlayInteractive) return; _overlayInteractive = false; OnPropertyChanged(nameof(OverlayInteractive)); }
    public string HistoryWarning => string.Join("\n", new[] { _historyConfigurationWarning,
        History.Status.Faulted || History.Status.Dropped > 0 || History.Status.CapacityWarning
        ? History.Status.Message + (History.Status.CapacityWarning ? " · 存档达到容量提醒阈值" : "") : "" }.Where(value => value.Length > 0));

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
    internal BiliImageCache Images { get; }
    internal UiImageSourceService UiImages => Images.Sources;
    private readonly ProcessResourceBudget _resourceBudget;
    public event Action<IReadOnlyList<LiveEvent>>? EventBatchReceived;
    public event Action? RoomHistoryReset;
    internal LiveEvent? LatestOnline { get; private set; }
    internal LiveEvent? LatestRank { get; private set; }
    internal LiveRoomProfile? RoomProfile { get; private set; }
    internal List<AudienceCountSample> AudienceHistory { get; } = new(240);
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
    public bool IsAnonymousReading => _settings.AnonymousReading || string.IsNullOrWhiteSpace(_runtimeCookies.Sessdata);
    public string ConnectionModeText => (_client.IsRunning ? "" : "未监视 · ") + (IsAnonymousReading ? $"匿名读取 · {(_settings.AnonymousReading ? "已选择匿名模式" : _accountReason)}" : $"账号读取 · {_accountName}");
    private CookieOptions ConnectionCookies => EffectiveCookies(_settings);
    private CookieOptions EffectiveCookies(AppSettings settings) => settings.AnonymousReading || _runtimeCookies.Sessdata.Length == 0 ? new() : _runtimeCookies;

    public LiveAssistantViewModel(AppSettings settings, ISettingsStore store, ILiveRoomClient client, ILiveInfoService info)
        : this(settings, store, client, info, null) { }
    internal LiveAssistantViewModel(AppSettings settings, ISettingsStore store, ILiveRoomClient client, ILiveInfoService info, BiliImageCache? images)
    {
        Images = images ?? new();
        _resourceBudget = new(Images.Sources);
        HistorySettings historySettings;
        try { historySettings = (settings.History ?? new()).Normalize(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.IOException)
        { historySettings = new(); _historyConfigurationWarning = "存档目录配置无效，已使用默认目录；请在可视化设置中重新保存目录。"; }
        _settings = settings with { Compact = (settings.Compact ?? new()).Normalize(), PriceDurations = (settings.PriceDurations ?? new()).Normalize(), Cookies = new(), TradeViews = (settings.TradeViews ?? new()).Normalize(), Dashboard = (settings.Dashboard ?? new()).Normalize(),
            MediaPreview = (settings.MediaPreview ?? new()).Normalize(), Cards = (settings.Cards ?? new()).Normalize(), History = historySettings };
        Dashboard.Cleared += CardBoard.Clear;
        Dashboard.Cleared += ClearHotPhrases;
        _store = store;
        _client = client;
        _info = info;
        _info.ConfigureAnalysis(_settings.History.Analysis, _settings.History.Enabled);
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _eventTimer = _dispatcher.CreateTimer();
        _authTimer = _dispatcher.CreateTimer(); _authTimer.IsRepeating = false; _authTimer.Interval = TimeSpan.FromSeconds(1);
        _hotTimer = _dispatcher.CreateTimer(); _hotTimer.Interval = TimeSpan.FromSeconds(1);
        _hotTimer.Tick += (_, _) => RefreshHotPhrases();
        _authTimer.Tick += async (_, _) => await ApplyAccountConnectionAsync();
        AccountSync = new(this, _dispatcher);
        if (store is JsonSettingsStore json && json.LoadWarning.Length > 0) ErrorText = json.LoadWarning;
        ApplyEventInterval();
        _eventTimer.Tick += FlushEvents;
        History = new SqliteLocalHistory(_settings.History);
        History.StatusChanged += OnHistoryStatus;
        _client.StatusChanged += OnStatus;
        Diagnostics = new(_dispatcher, CapturePerformanceSnapshot);
        CompactOverlay = new(this, _dispatcher);
    }

    internal object CapturePerformanceSnapshot()
    {
        return new
        {
            monitoring = IsMonitoring, connected = IsConnected, editing = OverlayInteractive, animatedEmotes = _settings.Dashboard.AnimateEmotes,
            roomResources = new { resets = _resourceResets, reason = _lastResetReason, offline = _roomOffline },
            received = ReceivedEventCount, pending = _pendingEvents.Count, bufferDropped = BufferedDroppedCount,
            overlayDropped = OverlayDroppedCount, overlayMedia = _overlayMediaCount,
            compact = CompactOverlay.GetDiagnostics(),
            dashboard = new { danmaku = Dashboard.Danmaku.Count, gifts = Dashboard.Gifts.Count,
                sc = Dashboard.SuperChats.Count, interactions = Dashboard.Interactions.Count, events = Events.Count },
            images = Images.GetDiagnostics(), avatars = Images.Avatars.GetDiagnostics(), imageResources = ImageResourceCounters.Snapshot(), uiImages = UiImages.GetDiagnostics(), media = Media.GetDiagnostics(),
            history = (History as SqliteLocalHistory)?.GetDiagnostics(),
            canvasBytes = PerformanceCounters.CanvasBytes, canvases = PerformanceCounters.Canvases,
            resourceBudget = _resourceBudget.Snapshot(),
            window = WindowDiagnostics?.Invoke(),
        };
    }

    public async Task StartAsync(int roomId)
    {
        if (_disposed) return;
        await _connectionGate.WaitAsync();
        try { await StartCoreAsync(roomId, true); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _monitorRequested = false; await StopCoreAsync(); CloseHistorySegment(); OnPropertyChanged(nameof(IsMonitoring)); ErrorText = "开启监视失败，请检查账号迁移提示或网络后重试"; }
        finally { _connectionGate.Release(); }
    }
    private async Task StartCoreAsync(int roomId, bool newMonitoring)
    {
        if (roomId <= 0) { ErrorText = "请输入有效的直播间ID"; return; }
        if (_client.IsRunning) return;
        if (newMonitoring)
        {
            ClearHotPhrases();
            CloseHistorySegment();
            _historyContext = History.Begin(roomId);
        }
        _monitorRequested = true;
        OnPropertyChanged(nameof(IsMonitoring));
        ErrorText = string.Empty;
        if (_settings.RoomId != roomId) ResetRoomHistory();
        _settings = _settings with { RoomId = roomId };
        await _store.SaveAsync(_settings);
        if (_disposed || !_monitorRequested) return;
        try
        {
            _connectionRun?.Dispose(); _connectionRun = new(); _startedCookies = ConnectionCookies;
            _roomOffline = _liveEnded = false;
            var run = _connectionRun; var context = _historyContext;
            LiveRoomProfile? receivedProfile = null;
            _networkHandler = (sender, value) =>
            {
                lock (_roomEvents)
                {
                    if (_disposed || run.IsCancellationRequested || !ReferenceEquals(run, _connectionRun)) return;
                    if (value.Extra.GetValueOrDefault("room_profile") is LiveRoomProfile observed) receivedProfile = observed;
                    if (value.Extra.GetValueOrDefault("live_status") is 0 && receivedProfile is { } known)
                    {
                        receivedProfile = known with { LiveStatus = 0 };
                        value = value with { Metadata = new Dictionary<string, object?>(value.Extra) { ["room_profile"] = receivedProfile } };
                    }
                    // Captured session and room travel with the event before display overflow or merging.
                    if (context is not null && !(value.Extra.GetValueOrDefault("sample") is true))
                        History.Capture(context, value, DateTimeOffset.UtcNow);
                    if (value.Extra.ContainsKey("archive_analysis")) return;
                    if (value.Extra.GetValueOrDefault("sample") is not true &&
                        (value.Extra.GetValueOrDefault("live_status") is int state && state is 0 or 2 ||
                         value.Extra.GetValueOrDefault("room_profile") is LiveRoomProfile { LiveStatus: 0 or 2 }))
                        _dispatcher.TryEnqueue(() => _ = StopEndedRoomAsync(run));
                    OnEvent(sender, value);
                }
            };
            _client.EventReceived += _networkHandler; _info.EventReceived += _networkHandler;
            await _client.StartAsync(roomId, _startedCookies, _connectionRun.Token);
            OnPropertyChanged(nameof(IsListening));
            await _info.StartAsync(roomId, _startedCookies, _settings.PollIntervalSeconds, _connectionRun.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await StopCoreAsync();
            _monitorRequested = false; CloseHistorySegment(); OnPropertyChanged(nameof(IsMonitoring));
            ErrorText = "连接失败，请检查网络后重新开启监视";
            Status = "连接失败";
        }
    }

    public async Task StopAsync()
    {
        _monitorRequested = false; _authTimer.Stop(); _connectionRun?.Cancel();
        await _connectionGate.WaitAsync();
        try
        {
            try { await StopCoreAsync(); }
            finally
            {
                CloseHistorySegment();
                // Stop preserves the current room for review; only a room change
                // or application shutdown releases its display/history resources.
                if (_disposed) ResetRoomHistory(2);
                OnPropertyChanged(nameof(IsMonitoring));
            }
        }
        finally { _connectionGate.Release(); }
    }
    private async Task StopCoreAsync()
    {
        _connectionRun?.Cancel();
        if (_networkHandler is not null) { _client.EventReceived -= _networkHandler; _info.EventReceived -= _networkHandler; _networkHandler = null; }
        await _info.StopAsync();
        await _client.StopAsync();
        OnPropertyChanged(nameof(IsListening));
    }

    private async Task StopEndedRoomAsync(CancellationTokenSource run)
    {
        await _connectionGate.WaitAsync();
        try
        {
            // A late signal from the previous room/reconnect must not stop a new run.
            if (_disposed || !_monitorRequested || !ReferenceEquals(run, _connectionRun) || run.IsCancellationRequested) return;
            _roomOffline = _liveEnded = true; _monitorRequested = false; _authTimer.Stop();
            if (RoomProfile is { } profile) { RoomProfile = profile with { LiveStatus = 0 }; OnPropertyChanged(nameof(RoomProfile)); }
            try { await StopCoreAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { ErrorText = "直播已关闭，连接清理异常，请重新开启监视前检查网络。"; }
            finally
            {
                CloseHistorySegment(); IsConnected = false;
                Status = "直播已关闭"; RoomSummary = "直播已关闭";
                if (!ErrorText.Contains("连接清理异常", StringComparison.Ordinal)) ErrorText = "直播已关闭，已自动停止监视。";
                OnPropertyChanged(nameof(IsMonitoring)); OnPropertyChanged(nameof(ConnectionModeText));
            }
        }
        finally { _connectionGate.Release(); }
    }

    // Resolve the current settings only after entering the gate: edits from separate
    // overlay frames must not overwrite each other's regions or account settings.
    internal async Task UpdateOverlayRegionAsync(OverlayRegionKind kind,
        Func<OverlayRegionSettings, OverlayRegionSettings> update, bool? enabled = null)
    {
        await _connectionGate.WaitAsync();
        try
        {
            var display = OverlayRegions.With(_settings.Display, kind, update(OverlayRegions.Get(_settings.Display, kind)).Normalize());
            if (enabled is { } visible) display = OverlayRegions.WithEnabled(display, kind, visible);
            await SaveCoreAsync(_settings with { Display = display });
        }
        finally { _connectionGate.Release(); }
    }

    internal async Task UpdateCompactAsync(Func<CompactOverlaySettings, CompactOverlaySettings> update)
    {
        await _connectionGate.WaitAsync();
        try { await SaveCoreAsync(_settings with { Compact = update(_settings.Compact).Normalize() }); }
        finally { _connectionGate.Release(); }
    }

    internal async Task LockOverlayLayoutAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            if (!_settings.Display.LayoutLocked)
                await SaveCoreAsync(_settings with { Display = _settings.Display with { LayoutLocked = true } });
        }
        finally { _connectionGate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _connectionGate.WaitAsync();
        try { await SaveCoreAsync(settings); } finally { _connectionGate.Release(); }
    }
    private async Task SaveCoreAsync(AppSettings settings)
    {
        var previous = _settings;
        settings = settings with { Compact = (settings.Compact ?? new()).Normalize(), PriceDurations = (settings.PriceDurations ?? new()).Normalize(), History = (settings.History ?? new()).Normalize() };
        var directoryChanged = !string.Equals(previous.History.EffectiveDirectory, settings.History.EffectiveDirectory, StringComparison.OrdinalIgnoreCase);
        if (directoryChanged && _monitorRequested) throw new InvalidOperationException("更换存档目录前请先停止监视");
        if (directoryChanged) await History.MoveAsync(settings.History.EffectiveDirectory, CancellationToken.None);
        var wasRunning = _client.IsRunning;
        var previousCookies = EffectiveCookies(previous);
        if (wasRunning && previous.RoomId != settings.RoomId) await StopCoreAsync();
        settings = settings with { Cookies = new(), TradeViews = (settings.TradeViews ?? new()).Normalize(), Dashboard = (settings.Dashboard ?? new()).Normalize(),
            MediaPreview = (settings.MediaPreview ?? new()).Normalize(), Cards = (settings.Cards ?? new()).Normalize() };
        _settings = settings;
        History.Configure(settings.History);
        _info.ConfigureAnalysis(settings.History.Analysis, settings.History.Enabled);
        if (previous.RoomId != settings.RoomId) ResetRoomHistory();
        Dashboard.Trim(settings.Dashboard);
        ApplyEventInterval();
        try { await _store.SaveAsync(settings); }
        catch
        {
            if (directoryChanged)
            {
                // Old database remains a complete backup. Reopen it if persisting the new path fails.
                await ((SqliteLocalHistory)History).UseExistingDirectoryAsync(previous.History.EffectiveDirectory);
            }
            _settings = previous; History.Configure(previous.History); _info.ConfigureAnalysis(previous.History.Analysis, previous.History.Enabled); throw;
        }
        OnPropertyChanged(nameof(Settings));
        _historyConfigurationWarning = ""; OnPropertyChanged(nameof(HistoryWarning));
        OnPropertyChanged(nameof(RoomId));
        OnPropertyChanged(nameof(IsAnonymousReading));
        OnPropertyChanged(nameof(ConnectionModeText));

        if (!wasRunning || !_monitorRequested) return;

        // Cookie 或房间变更后必须重建 WebSocket，避免继续使用旧的认证信息。
        if (previous.RoomId != settings.RoomId || previousCookies != ConnectionCookies)
        {
            AuthenticationChanged?.Invoke(); await StopCoreAsync();
            if (settings.RoomId > 0 && _monitorRequested && !_disposed)
                await StartCoreAsync(settings.RoomId, previous.RoomId != settings.RoomId);
            return;
        }

        if (previous.PollIntervalSeconds != settings.PollIntervalSeconds || previous.History.Analysis != settings.History.Analysis || previous.History.Enabled != settings.History.Enabled)
        {
            await _info.StopAsync();
            await _info.StartAsync(settings.RoomId, ConnectionCookies, settings.PollIntervalSeconds, _connectionRun?.Token ?? CancellationToken.None);
        }
    }

    internal void UpdateAccountCredentials(CookieOptions cookies, string name, string reason, bool stopMonitoring)
    {
        if (_disposed) return;
        var previous = ConnectionCookies; _runtimeCookies = cookies; _accountName = name; _accountReason = reason;
        OnPropertyChanged(nameof(IsAnonymousReading)); OnPropertyChanged(nameof(ConnectionModeText));
        if (stopMonitoring) { AuthenticationChanged?.Invoke(); _ = StopForAccountLossAsync(); return; }
        if (previous == ConnectionCookies) return;
        _credentialsRevision++; AuthenticationChanged?.Invoke();
        // Immediately cancel requests using the previous identity; combine rapid updates before rebuilding.
        if (_startedCookies.Sessdata != ConnectionCookies.Sessdata || _startedCookies.Buvid3 != ConnectionCookies.Buvid3) _connectionRun?.Cancel();
        _authTimer.Stop(); _authTimer.Start();
    }
    private async Task StopForAccountLossAsync()
    {
        try { await StopAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ErrorText = "停止监视未完成，请退出助手后重新打开"; }
    }
    private async Task ApplyAccountConnectionAsync()
    {
        _authTimer.Stop(); if (_disposed || _authApplying) return; _authApplying = true;
        var revision = _credentialsRevision;
        try
        {
            await _connectionGate.WaitAsync();
            try
            {
                if (!_disposed && _monitorRequested)
                {
                    if (_startedCookies.Sessdata != ConnectionCookies.Sessdata || _startedCookies.Buvid3 != ConnectionCookies.Buvid3 || _connectionRun?.IsCancellationRequested == true)
                    { await StopCoreAsync(); if (_monitorRequested && !_disposed) await StartCoreAsync(RoomId, false); }
                    else if (_startedCookies != ConnectionCookies)
                    { await _info.StopAsync(); _startedCookies = ConnectionCookies; await _info.StartAsync(RoomId, _startedCookies, Settings.PollIntervalSeconds, _connectionRun?.Token ?? CancellationToken.None); }
                }
            }
            finally { _connectionGate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ErrorText = "账号连接更新失败，请重新开启监视"; }
        finally { _authApplying = false; if (!_disposed && revision != _credentialsRevision) _authTimer.Start(); }
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
    private void CloseHistorySegment()
    {
        var context = Interlocked.Exchange(ref _historyContext, null);
        if (context is not null) History.End(context);
    }
    private void OnHistoryStatus(HistoryStatus status)
    {
        if (_disposed || Interlocked.CompareExchange(ref _historyNoticeScheduled, 1, 0) != 0) return;
        if (!_dispatcher.TryEnqueue(() => { Interlocked.Exchange(ref _historyNoticeScheduled, 0); if (!_disposed) OnPropertyChanged(nameof(HistoryWarning)); }))
            Interlocked.Exchange(ref _historyNoticeScheduled, 0);
    }
    internal async Task DeleteHistoryAsync(HistoryFilter filter, CancellationToken token)
    {
        await _connectionGate.WaitAsync(token);
        try { await History.DeleteAsync(filter, token); } finally { _connectionGate.Release(); }
    }
    internal async Task CompactHistoryAsync(CancellationToken token)
    {
        await _connectionGate.WaitAsync(token);
        try { if (IsMonitoring) throw new InvalidOperationException("请先停止监视"); await History.CompactAsync(token); }
        finally { _connectionGate.Release(); }
    }

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
        if (_settings.Compact.DesktopEnabled)
        {
            // Compact examples belong to its local editor, never the shared OBS timeline.
            if (!OverlayInteractive) ToggleOverlayInteraction();
            return;
        }
        void Example(LiveEvent value) => OnEvent(this, value with
            { Metadata = new Dictionary<string, object?>(value.Extra) { ["sample"] = true } });
        var small = new LiveEmote("[热]", "https://i0.hdslb.com/bfs/live/6df760280b17a6cbac8c1874d357298f982ba4cf.png", 20, 20);
        var appearance = new LiveMessageAppearance { MedalName = "示例牌", MedalLevel = 21, MedalColor = "#4388BC", GuardLevel = 3, IsAdmin = true, Emotes = new[] { small } };
        Example(new LiveEvent(LiveEventType.Danmaku, "图文示例用户", "这是一条带 B站图片表情的弹幕 [热]，也支持普通 emoji 😊", "#FFFFFF", 5, Appearance: appearance));
        Example(new LiveEvent(LiveEventType.Danmaku, "媒体接口示例", "BV1xx411c7mD · steam 730 · app/730（相同游戏去重，点击原页面才打开浏览器）", "#FFFFFF", 11));
        foreach (var uid in new long[] { 12, 13, 0 })
            Example(new LiveEvent(LiveEventType.Danmaku, uid > 0 ? $"重复示例用户{uid}" : "匿名示例", "同一句话原位更新计数 [热]", "#FFFFFF", uid, Appearance: appearance));
        Example(new LiveEvent(LiveEventType.Danmaku, "大表情示例", "[B站图片表情]", "#FFFFFF", 6,
            Appearance: new LiveMessageAppearance { MedalName = "示例牌", MedalLevel = 30,
                FullEmote = new("[B站图片表情]", "https://i0.hdslb.com/bfs/live/a98e35996545509188fe4d24bd1a56518ea5af48.png", 183, 60, true) }));
        Example(new LiveEvent(LiveEventType.Gift, "示例用户", "赠送 小心心×1", "#FF69B4", 1,
            Metadata: new Dictionary<string, object?> { ["gift_id"] = 1L, ["num"] = 1L, ["total_coin"] = 0L, ["coin_type"] = "silver" }));
        Example(new LiveEvent(LiveEventType.Gift, "示例用户", "赠送 小心心×2", "#FF69B4", 1,
            Metadata: new Dictionary<string, object?> { ["gift_id"] = 1L, ["num"] = 2L, ["total_coin"] = 0L, ["coin_type"] = "silver" }));
        Example(new LiveEvent(LiveEventType.Guard, "示例舰长", "开通了舰长！", "#FF69B4", 2));
        Example(new LiveEvent(LiveEventType.SuperChat, "示例留言", "¥30 这是一条醒目留言示例，可以单独调整滚动速度、字号和表情尺寸。", "#FFD700", 3));
        Example(new LiveEvent(LiveEventType.SuperChat, "长留言示例", "¥50 BV1xx411c7mD " + string.Concat(Enumerable.Repeat("长 SC 会连续分段滚动，同一条的各段不会插入其他留言 [热] 😊。", 8)), "#FFD700", 10,
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
        Example(new LiveEvent(LiveEventType.Enter, "新关注示例", "关注了主播", "#FF8AB3", 11,
            Appearance: new LiveMessageAppearance { AvatarUrl = entryAvatar }) { HistoryFields = new(InteractionKind: 2) });
        Example(new LiveEvent(LiveEventType.Enter, "点赞示例", "为主播点赞了", "#FFAA78", 12,
            Appearance: new LiveMessageAppearance { AvatarUrl = entryAvatar }) { HistoryFields = new(InteractionKind: 6) });
        var sample = new Dictionary<string, object?> { ["sample"] = true };
        Example(new LiveEvent(LiveEventType.Online, "示例系统", "示例人气：1234 · 看过：5678", "#2196F3", Metadata: sample));
        Example(new LiveEvent(LiveEventType.Rank, "示例系统", "高能榜: 1.示例观众甲 | 2.示例观众乙 | 3.示例观众丙", "#9C27B0",
            Metadata: new Dictionary<string, object?>(sample)
            {
                ["rank_names"] = Enumerable.Range(1, 10).Select(i => $"{i}.示例观众{i}").ToArray(),
                ["rank_total"] = 620L,
                ["rank_members"] = Enumerable.Range(1, 10).Select(i => new LiveRankMember(i, 10000 + i, $"示例观众{i}",
                    new() { AvatarUrl = entryAvatar, MedalName = "示例牌", MedalLevel = 20 + i, MedalColor = "#4388BC",
                        GuardLevel = i switch { 1 => 3, 2 => 2, 3 => 1, _ => 0 } })).ToArray(),
            }));
    }

    private void OnStatus(object? sender, string value)
    {
        var run = _connectionRun;
        var available = value == "已连接";
        _info.SetConnectionAvailable(available);
        var previous = Interlocked.Exchange(ref _streamAvailable, available ? 1 : 0);
        if (!available && previous == 1 && _monitorRequested && run?.IsCancellationRequested == false && _historyContext is { } context)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var kind in Enum.GetValues<HistorySampleKind>())
            {
                var sample = new HistoryAnalysisSample(kind, now, _settings.History.Analysis.CountsSeconds, null, null, null, null, null, [], 0, null, "连接中断");
                History.Capture(context, new LiveEvent(LiveEventType.Rank, "系统", "", "#FFFFFF", Metadata: new Dictionary<string, object?> { ["archive_analysis"] = sample }), now);
            }
        }
        void Update()
        {
            if (_disposed || !ReferenceEquals(run, _connectionRun) || run?.IsCancellationRequested == true && value == "已连接") return;
            if (_roomOffline && run?.IsCancellationRequested == true) return;
            Status = value;
            IsConnected = value == "已连接";
            if (!available && previous == 1) AddAudienceSample(DateTimeOffset.UtcNow, null);
            OnPropertyChanged(nameof(IsListening));
            OnPropertyChanged(nameof(ConnectionModeText));
        }
        if (_dispatcher.HasThreadAccess) Update();
        else _dispatcher.TryEnqueue(Update);
    }

    private void OnEvent(object? sender, LiveEvent value)
    {
        if (_disposed) return;
        if ((ReferenceEquals(sender, _client) || ReferenceEquals(sender, _info)) && _connectionRun?.IsCancellationRequested == true) return;
        if (value.Type is LiveEventType.Danmaku or LiveEventType.SuperChat && value.MediaReferences.Count == 0)
            value = value with { MediaReferences = MediaDetector.Detect(value.Content) };
        Interlocked.Increment(ref _receivedEvents);
        _hotWindow.Add(value, DateTimeOffset.UtcNow);
        _pendingEvents.Enqueue(value);
        ScheduleFlush();
    }

    internal void AcceptDashboardEnergyPage(LiveRoomProfile profile, DashboardRankPage page)
    {
        if (!_monitorRequested || !IsConnected || RoomProfile?.Uid != profile.Uid || RoomProfile?.RoomId != profile.RoomId) return;
        var value = new LiveEvent(LiveEventType.Rank, "系统", "高能榜", "#9C27B0", Metadata: new Dictionary<string, object?>
        { ["rank_members"] = page.Members.Take(10).ToArray(), ["rank_total"] = (long?)page.Total,
            ["rank_sampled_at"] = page.SampledAt, ["rank_room"] = profile.RoomId, ["rank_uid"] = profile.Uid });
        OnEvent(this, value);
    }

    private void ScheduleFlush()
    {
        // Keep one scheduled wake for the entire burst, not one dispatcher work
        // item per message. The timer stops as soon as the buffer is empty.
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0) return;
        if (!_dispatcher.TryEnqueue(() => { if (!_disposed && _pendingEvents.Count > 0) _eventTimer.Start(); }))
            Interlocked.Exchange(ref _flushScheduled, 0);
    }

    private void FlushEvents(DispatcherQueueTimer sender, object args)
    {
        if (_hotWindow.HasEntries && !_hotTimer.IsRunning) _hotTimer.Start();
        _resourceBudget.Observe();
        using var timing = PerformanceCounters.Measure(0);
        var batch = _pendingEvents.Drain();
        if (batch.Length > 0)
        {
            var accepted = new List<LiveEvent>(batch.Length);
            // Raw events have already been archived at ingress. Show only the
            // latest like per existing UI refresh, without another timer/queue.
            var lastLike = Array.FindLastIndex(batch, LiveInteractions.IsLike);
            for (var index = 0; index < batch.Length; index++)
            {
                var value = batch[index];
                if (index != lastLike && LiveInteractions.IsLike(value)) continue;
                var example = value.Extra.GetValueOrDefault("sample") is true;
                // Buffered live metrics may predate the reliable end signal.
                if (!example && _liveEnded && value.Type is LiveEventType.Online or LiveEventType.Rank) continue;
                if (!example && value.Type == LiveEventType.Online
                    && value.Extra.GetValueOrDefault("room_profile") is LiveRoomProfile liveProfile
                    && liveProfile.LiveStatus is { } liveState)
                {
                    _roomOffline = liveState is 0 or 2;
                }
                if (!example && _roomOffline && value.Type == LiveEventType.Rank) continue;
                if (value.Type == LiveEventType.Rank && value.Extra.GetValueOrDefault("rank_sampled_at") is DateTimeOffset incoming
                    && LatestRank?.Extra.GetValueOrDefault("rank_sampled_at") is DateTimeOffset latest && incoming <= latest) continue;
                if (example || !_roomOffline || value.Type != LiveEventType.Online) accepted.Add(value);
                // Local overlay examples must not replace real room metrics in the dashboard.
                if (value.Extra.TryGetValue("sample", out var sample) && sample is true) continue;
                Images.Avatars.Observe(value.Uid, value.Appearance?.AvatarUrl);
                if (value.Extra.GetValueOrDefault("rank_members") is IReadOnlyList<LiveRankMember> observedMembers)
                    foreach (var member in observedMembers) Images.Avatars.Observe(member.Uid, member.Appearance?.AvatarUrl);
                if (value.Type == LiveEventType.Online)
                {
                    if (value.Extra.TryGetValue("popularity", out var popularity) && popularity is string p) _popularity = p;
                    if (value.Extra.TryGetValue("watched", out var watched) && watched is string { Length: > 0 } w) _watched = w;
                    var metrics = new List<string>();
                    if (_popularity.Length > 0) metrics.Add("人气: " + _popularity);
                    if (_watched.Length > 0) metrics.Add("看过: " + _watched.Replace("人看过", "", StringComparison.Ordinal));
                    OnlineText = metrics.Count > 0 ? string.Join(" · ", metrics) : value.Content;
                    if (!_roomOffline) LatestOnline = value with { Content = OnlineText,
                        Metadata = new Dictionary<string, object?>(value.Extra) { ["popularity"] = _popularity, ["watched"] = _watched } };
                    if (value.Extra.TryGetValue("room_summary", out var summary) && summary is string text) RoomSummary = text;
                    if (value.Extra.TryGetValue("room_profile", out var profile) && profile is LiveRoomProfile suppliedProfile)
                    {
                        RoomProfile = suppliedProfile with { EnergyMembers = LatestRank?.Extra.GetValueOrDefault("rank_total") as long? };
                        OnPropertyChanged(nameof(RoomProfile));
                    }
                }
                if (value.Type == LiveEventType.Rank)
                {
                    LatestRank = value; RankText = value.Content;
                    var count = value.Extra.GetValueOrDefault("rank_total") as long?;
                    if (RoomProfile is { } profile)
                    { RoomProfile = profile with { EnergyMembers = count }; OnPropertyChanged(nameof(RoomProfile)); }
                    if (IsListening) AddAudienceSample(value.OccurredAt, count is >= 0 ? count : null);
                }
            }
            batch = accepted.ToArray();
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

    private void AddAudienceSample(DateTimeOffset time, long? value)
    {
        // Rank observations only; popularity and loaded member count are different metrics.
        if (AudienceHistory.Count > 0 && time <= AudienceHistory[^1].Time) time = AudienceHistory[^1].Time.AddTicks(1);
        if (AudienceHistory.Count >= 240) AudienceHistory.RemoveAt(0);
        AudienceHistory.Add(new(time, value));
        OnPropertyChanged(nameof(AudienceHistory));
    }

    private void RefreshHotPhrases()
    {
        var next = _hotWindow.Snapshot(DateTimeOffset.UtcNow);
        if (!HotPhrases.SequenceEqual(next)) { HotPhrases = next; HotPhrasesChanged?.Invoke(); }
        if (!_hotWindow.HasEntries) _hotTimer.Stop();
    }
    private void ClearHotPhrases()
    {
        _hotWindow.Clear(); _hotTimer.Stop();
        if (HotPhrases.Count == 0) return;
        HotPhrases = Array.Empty<HotPhrase>(); HotPhrasesChanged?.Invoke();
    }

    private void ResetRoomHistory(int reason = 1)
    {
        // Serialize with ingress so an old callback cannot enqueue after clearing.
        lock (_roomEvents) _pendingEvents.Clear();
        _eventTimer.Stop(); Interlocked.Exchange(ref _flushScheduled, 0);
        _resourceResets++; _lastResetReason = reason; _roomOffline = false;
        Events.Clear(); Dashboard.Clear();
        LatestOnline = null; LatestRank = null;
        RoomProfile = null; AudienceHistory.Clear(); OnPropertyChanged(nameof(RoomProfile));
        _popularity = ""; _watched = "";
        RoomHistoryReset?.Invoke();
        Images.ResetRoom(); Media.ResetRoom(); BiliDashboardRankService.InvalidateCache();
        OnlineText = "--"; RankText = "暂无榜单"; RoomSummary = "房间状态暂无数据";
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await CompactOverlay.DisposeAsync();
        await Diagnostics.DisposeAsync(); WindowDiagnostics = null;
        _authTimer.Stop(); await AccountSync.DisposeAsync();
        ClearHotPhrases();
        _eventTimer.Stop();
        _eventTimer.Tick -= FlushEvents;
        _client.StatusChanged -= OnStatus;
        _pendingEvents.Clear();
        await StopAsync();
        _resourceBudget.Dispose();
        Images.Dispose();
        Media.Dispose(); Dashboard.Cleared -= CardBoard.Clear;
        Dashboard.Cleared -= ClearHotPhrases; HotPhrasesChanged = null;
        History.StatusChanged -= OnHistoryStatus;
        await History.DisposeAsync();
        _connectionRun?.Dispose();
        if (_client is IDisposable disposable) disposable.Dispose();
        if (_info is IDisposable info) info.Dispose();
    }
}
