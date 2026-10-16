using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using BilibiliLiveAssistant.Core;
using BilibiliLiveAssistant.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BilibiliLiveAssistant.App;

// Linked against the production UI, image and archive code. Only the transport,
// image HTTP handler and settings store are replaced. Never opens user history.
public partial class App : Application
{
    public static LiveAssistantViewModel ViewModel { get; private set; } = null!;
    internal static bool AttachImageSources { get; private set; } = true;
    internal static bool CreateImageSources { get; private set; } = true;
    internal static bool UseDrawingDecode { get; private set; }
    internal static string DiagnosticDirectory { get; private set; } = null!;
    private MainWindow? _window;
    internal static nint MainWindowHandle => Current is App app && app._window is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0;
    private readonly SoakConfig _config;
    private readonly SoakClient _client;
    private readonly Stopwatch _elapsed = new();
    private readonly CancellationTokenSource _stop = new();
    private DispatcherQueueTimer? _sampling;
    private StreamWriter? _log;
    private long _produced;
    private string _phase = "warmup";
    private Process? _process;
    private double _previousSeconds, _previousCpu;
    private bool _finishing;
    private Task _snapshot = Task.CompletedTask;
    private Task _scrollProbe = Task.CompletedTask;
    private readonly List<WeakReference> _retired = new();
    public App()
    {
        InitializeComponent();
        _config = JsonSerializer.Deserialize<SoakConfig>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "soak-config.json")))!;
        DiagnosticDirectory = _config.Output;
        _client = new(_config.LiveRoom > 0);
        AttachImageSources = _config.AttachImages;
        CreateImageSources = _config.CreateSources;
        UseDrawingDecode = _config.DrawingDecode;
        Directory.CreateDirectory(_config.Output);
        UnhandledException += (_, e) =>
        {
            File.WriteAllText(Path.Combine(_config.Output, "fatal.txt"), e.Exception.ToString());
            Environment.Exit(2);
        };
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _log = new StreamWriter(Path.Combine(_config.Output, "samples.jsonl"), false) { AutoFlush = true };
            var live = _config.LiveRoom > 0;
            var settings = new AppSettings { RoomId = live ? _config.LiveRoom : 900000001, AnonymousReading = true,
                History = new() { Directory = Path.Combine(_config.Output, "history") },
                Dashboard = new() { RankPanelCollapsed = true, ShowEntryAvatars = _config.EntryAvatars, AnimateEmotes = _config.AnimateEmotes, ShowAvatars = _config.Images is not ("none" or "emotes") } };
            HttpMessageHandler? handler = live ? null : _config.FixturePort > 0 ? new LoopbackFixtureImages(_config.FixturePort, _config.FixtureCount) : new FixtureImages();
            var images = new BiliImageCache(handler, Path.Combine(_config.Output, "images"));
            ViewModel = new(settings, new SoakStore(), _client, live ? new BiliLiveInfoService() : new SoakInfo(), images);
            _window = new(ViewModel) { Title = "内存模拟 · " + _config.Name };
            _window.Activate();
            if (_config.View is not ("dashboard" or "pipeline")) _window.OpenOverlay();
            if (_config.View is "overlay" or "pipeline") _window.HideToTray();
            _process = Process.GetCurrentProcess();
            _previousCpu = _process.TotalProcessorTime.TotalSeconds;
            _elapsed.Start();
            PerformanceCounters.Enabled = true;
            _sampling = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _sampling.Interval = TimeSpan.FromSeconds(5); _sampling.Tick += (_, _) => Sample(); _sampling.Start();
            _log.WriteLine(JsonSerializer.Serialize(new { kind = "header", config = _config, pid = Environment.ProcessId,
                runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount,
                assembly = typeof(App).Assembly.GetName().Name, dpi = _window.Content.XamlRoot?.RasterizationScale ?? GetDpiForWindow(MainWindowHandle) / 96d,
                winUiVersion = _process.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName.Equals("Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase))?.FileVersionInfo.FileVersion,
                build = File.Exists(Path.Combine(AppContext.BaseDirectory, "soak-build.json"))
                    ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "soak-build.json"))) : (JsonElement?)null,
                timestamp = DateTimeOffset.UtcNow, simulatedImages = !live, realArchive = _config.View != "pipeline", realRendering = _config.View != "pipeline",
                note = live ? "Anonymous live transport; isolated archive and image cache; no user settings, cookies or BrowserHost."
                    : "No external network, browser login, user settings or user history. Extra rank-panel HTTP disabled." }));
            Sample();
            _phase = "startup_baseline";
            await Task.Delay(TimeSpan.FromSeconds(_config.BaselineSeconds));
            if (_config.StartupOnly) { await FinishAsync(); return; }
            if (_config.InteractionProbe) await VerifyInteractionBurstAsync();
            await ViewModel.StartAsync(settings.RoomId);
            await Task.Delay(TimeSpan.FromSeconds(_config.WarmupSeconds));
            _phase = "traffic";
            if (_config.Snapshot) _snapshot = CaptureOverviewAsync();
            if (_config.ScrollProbe) _scrollProbe = VerifyScrollAsync();
            if (live)
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.Seconds), _stop.Token);
                await ViewModel.StopAsync();
                await FinishAsync();
                return;
            }
            if (_config.View == "pipeline")
            {
                // Diagnostic only: exercise exactly the production cache/decode
                // and optional UI source factories, with no message visual tree.
                var start = Stopwatch.GetTimestamp();
                using var cadence = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                while (Stopwatch.GetElapsedTime(start).TotalSeconds < _config.Seconds && await cadence.WaitForNextTickAsync(_stop.Token))
                {
                    var expected = (long)(Stopwatch.GetElapsedTime(start).TotalSeconds * _config.Rate);
                    var tasks = new List<Task>();
                    while (_produced < expected && tasks.Count < 32)
                    {
                        var sequence = ++_produced;
                        tasks.Add(ExerciseImagePipelineAsync(sequence));
                    }
                    await Task.WhenAll(tasks);
                }
                _log.WriteLine(JsonSerializer.Serialize(new { kind = "diagnostic", pipelineOnly = true,
                    note = "Not a rendering, archive, retention or two-hour acceptance run." }));
                await FinishAsync();
                return;
            }
            await Task.Run(async () =>
            {
                var start = Stopwatch.GetTimestamp();
                using var cadence = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                long sent = 0; var lastProfile = -1; double expectedTotal = 0, lastElapsed = 0;
                while (Stopwatch.GetElapsedTime(start).TotalSeconds < _config.Seconds && await cadence.WaitForNextTickAsync(_stop.Token))
                {
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    var phaseSeconds = elapsed % 120;
                    var rate = _config.Pattern == "intermittent" ? phaseSeconds < 30 ? 0 : phaseSeconds < 90 ? 3 : 50 : _config.Rate;
                    expectedTotal += Math.Max(0, elapsed - lastElapsed) * rate; lastElapsed = elapsed;
                    var expected = (long)expectedTotal;
                    // Bounded catch-up, to avoid unlimited synthetic backlog if rendering stalls.
                    var count = Math.Min(5000, expected - sent);
                    for (var i = 0; i < count; i++) Emit(++sent);
                    if ((int)elapsed / 30 != lastProfile) { lastProfile = (int)elapsed / 30; EmitRoom(settings.RoomId); }
                }
            });
            if (_config.LifecycleProbe) await VerifyVisibilityAsync();
            // Let the normal display batch finish before taking retention checks.
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (_config.Snapshot) await CaptureOverviewAsync("overview-settled.png", 0);
            var retainedIds = ViewModel.Dashboard.Danmaku.Concat(ViewModel.Dashboard.Gifts).Concat(ViewModel.Dashboard.SuperChats).Select(r => r.Id).ToHashSet();
            EmitRoom(settings.RoomId, 0);
            await Task.Delay(TimeSpan.FromSeconds(2));
            var offlineRetainsContent = retainedIds.IsSubsetOf(ViewModel.Dashboard.Danmaku.Concat(ViewModel.Dashboard.Gifts).Concat(ViewModel.Dashboard.SuperChats).Select(r => r.Id));
            _phase = "before_stop"; Sample();
            var retained = ViewModel.Dashboard.Danmaku.Count + ViewModel.Dashboard.Gifts.Count + ViewModel.Dashboard.SuperChats.Count;
            await ViewModel.StopAsync();
            _phase = "stopped_retained"; Sample();
            var afterStop = ViewModel.Dashboard.Danmaku.Count + ViewModel.Dashboard.Gifts.Count + ViewModel.Dashboard.SuperChats.Count;
            await Task.Delay(TimeSpan.FromSeconds(20));
            // A new monitoring segment in the same room must not erase the overview.
            await ViewModel.StartAsync(settings.RoomId);
            var sameRoom = ViewModel.Dashboard.Danmaku.Count + ViewModel.Dashboard.Gifts.Count + ViewModel.Dashboard.SuperChats.Count;
            var sameRoomRetainsContent = retainedIds.IsSubsetOf(ViewModel.Dashboard.Danmaku.Concat(ViewModel.Dashboard.Gifts).Concat(ViewModel.Dashboard.SuperChats).Select(r => r.Id));
            await ViewModel.StopAsync();
            foreach (var record in ViewModel.Dashboard.Danmaku.Take(10)) _retired.Add(new(record));
            await ViewModel.SaveAsync(ViewModel.Settings with { RoomId = settings.RoomId + 1 });
            var switched = ViewModel.Dashboard.Danmaku.Count + ViewModel.Dashboard.Gifts.Count + ViewModel.Dashboard.SuperChats.Count;
            _phase = "room_switched"; Sample();
            await Task.Delay(TimeSpan.FromSeconds(20));
            _log.WriteLine(JsonSerializer.Serialize(new { kind = "assertions", retainedBeforeStop = retained, retainedAfterStop = afterStop,
                retainedSameRoomRestart = sameRoom, previousRoomAfterSwitch = switched,
                stopRetainsContent = retained > 0 && afterStop == retained, sameRoomRetainsContent,
                offlineRetainsContent,
                switchClearsContent = switched == 0, retiredRecordWeakAlive = _retired.Count(r => r.IsAlive) }));
            await FinishAsync();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(_config.Output, "fatal.txt"), ex.ToString());
            Environment.Exit(2);
        }
    }
    private async Task ExerciseImagePipelineAsync(long sequence)
    {
        var url = $"https://i0.hdslb.com/soak/avatar/{sequence % _config.Users}.png";
        if (_config.CreateSources)
        {
            using var lease = await ViewModel.Images.Sources.AcquireStaticSourceAsync(url, 32, 64, _stop.Token);
        }
        else await ViewModel.Images.GetStaticForViewAsync(url, _stop.Token, 64);
    }
    private void Emit(long sequence)
    {
        Interlocked.Increment(ref _produced);
        var uid = sequence % _config.Users + 1;
        var type = sequence % 401 == 0 ? LiveEventType.Guard : sequence % 100 == 0 ? LiveEventType.SuperChat
            : sequence % 17 == 0 ? LiveEventType.Gift : sequence % 7 == 0 ? LiveEventType.Enter : LiveEventType.Danmaku;
        var emote = sequence % 3 == 0 && _config.Images is not ("none" or "avatars");
        var imageId = _config.Images == "shared" ? uid % 8 : uid;
        var avatarUrl = _config.FixturePort > 0 ? $"https://i0.hdslb.com/bfs/face/{imageId:x40}.jpg" : $"https://i0.hdslb.com/soak/avatar/{imageId}.png";
        var appearance = new LiveMessageAppearance { AvatarUrl = _config.Images == "none" ? "" : avatarUrl, MedalName = "测试团",
            MedalLevel = (int)(uid % 30 + 1), GuardLevel = uid % 20 == 0 ? 3 : 0,
            Emotes = emote ? [new("[表情]", $"https://i0.hdslb.com/soak/emote/{sequence % 128}.png", 48, 48)] : [] };
        var body = type == LiveEventType.SuperChat ? string.Join("，", Enumerable.Repeat("这是需要完整展示的醒目留言，感谢主播", 8))
            : $"模拟消息 {sequence}，今天天气不错，直播内容很好看" + (emote ? "[表情] 欢迎 [表情]" : "");
        var fields = type is LiveEventType.Gift or LiveEventType.Guard ? new HistoryEventFields(UniqueId: "g" + sequence,
            GiftId: 1, GiftName: type == LiveEventType.Guard ? "舰长" : "鲜花", Quantity: 1, CoinType: "gold", UnitPrice: 100, TotalCoin: 100)
            : type == LiveEventType.SuperChat ? new HistoryEventFields(UniqueId: "sc" + sequence, AmountMilliYuan: 30000) : null;
        _client.Emit(new(type, $"模拟用户{uid}", body, "#FFFFFF", uid, Appearance: appearance) { HistoryFields = fields });
    }
    private void EmitRoom(int room, int liveStatus = 1)
    {
        var profile = new LiveRoomProfile(100000, room, "模拟主播", "https://i0.hdslb.com/soak/avatar/anchor.png", "隔离压力模拟", "测试团", 20000, 200, 100000, 3000)
            { LiveStatus = liveStatus, LiveStartedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        _client.Emit(new(LiveEventType.Online, "系统", "人数模拟", "#FFFFFF", Metadata: new Dictionary<string, object?> { ["room_profile"] = profile }));
        var members = Enumerable.Range(1, 10).Select(i => new LiveRankMember(i, i, $"模拟榜单{i}", new() { AvatarUrl = $"https://i0.hdslb.com/soak/avatar/{i}.png", MedalName = "测试团", MedalLevel = 20, GuardLevel = 3 })).ToArray();
        _client.Emit(new(LiveEventType.Rank, "系统", "高能榜", "#FFFFFF", Metadata: new Dictionary<string, object?>
            { ["rank_members"] = members, ["rank_total"] = 3000L, ["rank_sampled_at"] = DateTimeOffset.UtcNow }));
    }
    private void Sample()
    {
        if (_process is null || _finishing) return;
        _process.Refresh(); var seconds = _elapsed.Elapsed.TotalSeconds; var cpu = _process.TotalProcessorTime.TotalSeconds;
        var gc = GC.GetGCMemoryInfo();
        var native = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        var hasNativeMemory = GetProcessMemoryInfo(_process.Handle, ref native, native.Size);
        _log!.WriteLine(JsonSerializer.Serialize(new { kind = "sample", phase = _phase, seconds, produced = Interlocked.Read(ref _produced),
            privateBytes = _process.PrivateMemorySize64, workingSetBytes = _process.WorkingSet64, managedBytes = GC.GetTotalMemory(false),
            privateWorkingSetBytes = hasNativeMemory ? (ulong?)native.PrivateWorkingSetSize : null,
            gcHeapBytes = gc.HeapSizeBytes, fragmentedBytes = gc.FragmentedBytes,
            allocatedBytes = GC.GetTotalAllocatedBytes(false), gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
            cpuPercent = seconds > _previousSeconds ? 100 * (cpu - _previousCpu) / (seconds - _previousSeconds) / Environment.ProcessorCount : 0,
            handles = _process.HandleCount, gdi = GetGuiResources(_process.Handle, 0), user = GetGuiResources(_process.Handle, 1),
            ui = ViewModel.CapturePerformanceSnapshot(), timing = PerformanceCounters.Drain() }));
        _previousSeconds = seconds; _previousCpu = cpu;
    }
    private async Task FinishAsync()
    {
        await _snapshot; await _scrollProbe;
        _phase = "finished"; Sample(); _finishing = true; _stop.Cancel(); _sampling?.Stop();
        _window?.AllowClose(); await ViewModel.DisposeAsync(); _window?.Close();
        _log?.Dispose(); _process?.Dispose();
        File.WriteAllText(Path.Combine(_config.Output, "completed.json"), JsonSerializer.Serialize(new { seconds = _elapsed.Elapsed.TotalSeconds, produced = _produced }));
        Exit();
    }
    private async Task VerifyVisibilityAsync()
    {
        var checkpoints = new List<object>();
        for (var i = 0; i < 30; i++)
        {
            _phase = "lifecycle_hidden"; _window!.HideToTray();
            await Task.Delay(250);
            _phase = "lifecycle_shown"; _window.ShowMainWindow();
            await Task.Delay(500);
            checkpoints.Add(new { cycle = i + 1, resources = ImageResourceCounters.Snapshot(), sources = ViewModel.UiImages.GetDiagnostics() });
        }
        _phase = "lifecycle_hidden"; _window!.HideToTray();
        await Task.Delay(TimeSpan.FromSeconds(20));
        var hiddenSources = JsonSerializer.SerializeToElement(ViewModel.UiImages.GetDiagnostics());
        var hidden = new { sources = hiddenSources, resources = ImageResourceCounters.Snapshot(),
            noActiveViews = Interlocked.Read(ref ImageResourceCounters.ActiveViews) == 0,
            noLeases = hiddenSources.GetProperty("leases").GetInt32() == 0,
            noPending = hiddenSources.GetProperty("pending").GetInt32() == 0,
            noSources = hiddenSources.GetProperty("sources").GetInt32() == 0 };
        _phase = "lifecycle_shown"; _window.ShowMainWindow(); await Task.Delay(1000);
        File.WriteAllText(Path.Combine(_config.Output, "visibility-check.json"), JsonSerializer.Serialize(new { checkpoints, hidden }));
    }
    private async Task VerifyScrollAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(25));
            var results = new List<object>();
            foreach (var title in new[] { "SC", "弹幕" })
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var target = FindChildren<DashboardList>((DependencyObject)_window!.Content)
                    .First(row => (string?)typeof(DashboardList).GetField("_title", flags)!.GetValue(row) == title);
                var list = (Microsoft.UI.Xaml.Controls.ListView)typeof(DashboardList).GetField("_list", flags)!.GetValue(target)!;
                var items = (System.Collections.IList)typeof(DashboardList).GetField("_items", flags)!.GetValue(target)!;
                var viewer = FindChildren<Microsoft.UI.Xaml.Controls.ScrollViewer>(list).First();
                // Low-rate runs may not have four SCs at the original 25s mark.
                // Wait for real fixture messages instead of indexing absent rows.
                var deadline = Stopwatch.GetTimestamp();
                while (items.Count < (title == "SC" ? 3 : 31) && Stopwatch.GetElapsedTime(deadline).TotalSeconds < 100)
                    await Task.Delay(500, _stop.Token);
                if (items.Count < 2) throw new InvalidOperationException("阅读位置检查的消息样本不足");
                list.ScrollIntoView(items[Math.Min(items.Count - 1, title == "SC" ? 3 : 30)], Microsoft.UI.Xaml.Controls.ScrollIntoViewAlignment.Leading);
                await Task.Delay(700);
                viewer.ChangeView(null, viewer.VerticalOffset + (title == "SC" ? 120 : 20), null, true);
                await Task.Delay(300);
                (Guid Id, double Pixels) ReadAnchor()
                {
                    var panel = (Microsoft.UI.Xaml.Controls.ItemsStackPanel)list.ItemsPanelRoot;
                    var index = panel.FirstVisibleIndex;
                    if (index < 0 || index >= items.Count) throw new InvalidOperationException("阅读位置检查尚未取得可见行");
                    var slot = items[index]!;
                    var record = slot is DashboardRecord direct ? direct : (DashboardRecord)slot.GetType().GetField("Record")!.GetValue(slot)!;
                    var container = (FrameworkElement)list.ContainerFromIndex(index);
                    return (record.Id, viewer.VerticalOffset - container.ActualOffset.Y);
                }
                var before = ReadAnchor();
                await Task.Delay(TimeSpan.FromSeconds(title == "SC" ? 8 : 3));
                var after = ReadAnchor();
                results.Add(new { title, sameRecord = before.Id == after.Id, beforePixels = before.Pixels,
                    afterPixels = after.Pixels, samePixelOffset = Math.Abs(before.Pixels - after.Pixels) < 2 });
                list.ScrollIntoView(items[0], Microsoft.UI.Xaml.Controls.ScrollIntoViewAlignment.Leading);
                viewer.ChangeView(null, 0, null, true);
            }
            File.WriteAllText(Path.Combine(_config.Output, "scroll-check.json"), JsonSerializer.Serialize(results));
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_config.Output, "scroll-error.txt"), error.ToString()); }
    }
    private static IEnumerable<T> FindChildren<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) yield return match;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in FindChildren<T>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    private async Task CaptureOverviewAsync(string filename = "overview.png", int delaySeconds = 20)
    {
        try
        {
            if (delaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync((UIElement)_window!.Content);
            var pixels = await bitmap.GetPixelsAsync();
            using var file = File.Create(Path.Combine(_config.Output, filename));
            using var output = file.AsRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_config.Output, "snapshot-error.txt"), error.ToString()); }
    }
    private async Task VerifyInteractionBurstAsync()
    {
        var checks = new List<object>();
        foreach (var avatars in new[] { true, false })
        {
            await ViewModel.SaveAsync(ViewModel.Settings with { Dashboard = ViewModel.Settings.Dashboard with { ShowEntryAvatars = avatars } });
            for (var batch = 0; batch < 4; batch++)
            {
                var entries = Enumerable.Range(0, 30).Select(i => new LiveEvent(LiveEventType.Enter,
                    $"进房验证{batch * 30 + i}", "进入直播间", "#FFFFFF", batch * 30 + i + 1)).ToArray();
                ViewModel.Dashboard.AddBatch(entries, ViewModel.Settings.Dashboard);
                await Task.Delay(600);
                var lists = FindChildren<Microsoft.UI.Xaml.Controls.ListView>(_window!.Content);
                var cards = lists.SelectMany(l => FindChildren<MessageCard>(l)).Where(c => c.RecordId != Guid.Empty).ToArray();
                var entriesVisible = cards.Count(c => c.HasBoundText);
                checks.Add(new { avatars, batch, retained = ViewModel.Dashboard.Interactions.Count, realized = cards.Length, withText = entriesVisible,
                    passed = cards.Length > 0 && entriesVisible == cards.Length });
            }
            await CaptureOverviewAsync(avatars ? "interaction-avatars.png" : "interaction-no-avatars.png", 0);
        }
        File.WriteAllText(Path.Combine(_config.Output, "interaction-check.json"), JsonSerializer.Serialize(checks));
        ViewModel.Dashboard.Clear();
        await ViewModel.SaveAsync(ViewModel.Settings with { Dashboard = ViewModel.Settings.Dashboard with { ShowEntryAvatars = _config.EntryAvatars } });
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    // Layout from the installed Windows SDK psapi.h PROCESS_MEMORY_COUNTERS_EX2.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
            PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCounters counters, uint size);
}
internal sealed record SoakConfig(string Output, string Name = "baseline", int Seconds = 180, int Rate = 50, int Users = 20000, int WarmupSeconds = 10, string View = "both", bool Snapshot = false, string Images = "unique", bool AttachImages = true, bool CreateSources = true, bool DrawingDecode = false, bool ScrollProbe = false, int LiveRoom = 0, int BaselineSeconds = 60, bool EntryAvatars = true, int FixturePort = 0, int FixtureCount = 11, string Pattern = "steady", bool StartupOnly = false, bool LifecycleProbe = false, bool AnimateEmotes = false, bool InteractionProbe = false);
internal sealed class SoakStore : ISettingsStore
{
    public string Path => "isolated-memory-only";
    public AppSettings Load() => new();
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
internal sealed class SoakClient : ILiveRoomClient
{
    private readonly BiliLiveClient? _live;
    private bool _running;
    public SoakClient(bool live)
    {
        if (!live) return;
        _live = new();
        _live.EventReceived += (_, e) => EventReceived?.Invoke(this, e);
        _live.StatusChanged += (_, e) => StatusChanged?.Invoke(this, e);
    }
    public event EventHandler<LiveEvent>? EventReceived;
    public event EventHandler<string>? StatusChanged;
    public bool IsRunning => _live?.IsRunning ?? _running;
    public Task StartAsync(int roomId, CookieOptions cookies, CancellationToken cancellationToken = default)
    { if (_live is not null) return _live.StartAsync(roomId, new(), cancellationToken); _running = true; StatusChanged?.Invoke(this, "本地模拟（无网络）"); return Task.CompletedTask; }
    public Task StopAsync() { if (_live is not null) return _live.StopAsync(); _running = false; StatusChanged?.Invoke(this, "已断开连接"); return Task.CompletedTask; }
    public void Emit(LiveEvent value) { if (_live is null && IsRunning) EventReceived?.Invoke(this, value); }
}
internal sealed class SoakInfo : ILiveInfoService
{
    public event EventHandler<LiveEvent>? EventReceived { add { } remove { } }
    public void ConfigureAnalysis(HistoryAnalysisSettings settings, bool enabled) { }
    public void SetConnectionAvailable(bool available) { }
    public Task StartAsync(int roomId, CookieOptions cookies, int intervalSeconds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}
internal sealed class FixtureImages : HttpMessageHandler
{
    private readonly byte[][] _fixtures = Enumerable.Range(0, 8).Select(Create).ToArray();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != "i0.hdslb.com" || !request.RequestUri.AbsolutePath.StartsWith("/soak/", StringComparison.Ordinal))
            throw new InvalidOperationException("模拟禁止外部请求");
        await Task.Delay(5, cancellationToken);
        var index = (uint)StringComparer.Ordinal.GetHashCode(request.RequestUri.AbsolutePath) % (uint)_fixtures.Length;
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(_fixtures[index]) };
    }
    private static byte[] Create(int seed)
    {
        using var bitmap = new Bitmap(128, 128, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(230, 40 + seed * 20, 180 - seed * 10, 150));
            graphics.FillEllipse(brush, 2, 2, 124, 124);
            using var accent = new Pen(Color.White, 4); graphics.DrawEllipse(accent, 30, 30, 24, 24); graphics.DrawArc(accent, 25, 55, 70, 45, 0, 180);
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }
}

// Test-only URI mapping. The production whitelist, TLS and HTTP configuration
// remain unchanged; body reads use a real loopback socket and chunked responses.
internal sealed class LoopbackFixtureImages(int port, int count) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _network = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false });
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (port is <= 0 or > 65535 || count <= 0 || request.RequestUri?.Host != "i0.hdslb.com") throw new InvalidOperationException("模拟地址无效");
        var path = request.RequestUri.AbsolutePath;
        long identity;
        if (path.StartsWith("/bfs/face/", StringComparison.Ordinal))
        {
            var name = path[10..].Split('.')[0];
            identity = long.Parse(name[^Math.Min(12, name.Length)..], System.Globalization.NumberStyles.HexNumber);
        }
        else if (path.StartsWith("/soak/", StringComparison.Ordinal))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            identity = long.TryParse(name, out var number) ? number : 0;
        }
        else throw new InvalidOperationException("模拟禁止外部请求");
        var size = path.Contains('@') ? path[(path.IndexOf('@') + 1)..].Split('w')[0] : "";
        var suffix = size is "64" or "128" or "256" ? "?size=" + size : "";
        using var mapped = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/image/{identity % count}{suffix}");
        return await _network.SendAsync(mapped, cancellationToken);
    }
    protected override void Dispose(bool disposing) { if (disposing) _network.Dispose(); base.Dispose(disposing); }
}
