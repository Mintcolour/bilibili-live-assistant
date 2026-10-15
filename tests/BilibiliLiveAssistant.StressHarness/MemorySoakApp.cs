using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using BilibiliLiveAssistant.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BilibiliLiveAssistant.App;

// Linked against the production UI, image and archive code. Only the transport,
// image HTTP handler and settings store are replaced. Never opens user history.
public partial class App : Application
{
    public static LiveAssistantViewModel ViewModel { get; private set; } = null!;
    private MainWindow? _window;
    internal static nint MainWindowHandle => Current is App app && app._window is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0;
    private readonly SoakConfig _config;
    private readonly SoakClient _client = new();
    private readonly Stopwatch _elapsed = new();
    private readonly CancellationTokenSource _stop = new();
    private DispatcherQueueTimer? _sampling;
    private StreamWriter? _log;
    private long _produced;
    private string _phase = "warmup";
    private Process? _process;
    private double _previousSeconds, _previousCpu;
    private bool _finishing;
    private readonly List<WeakReference> _retired = new();
    public App()
    {
        InitializeComponent();
        _config = JsonSerializer.Deserialize<SoakConfig>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "soak-config.json")))!;
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
            var settings = new AppSettings { RoomId = 900000001, AnonymousReading = true,
                History = new() { Directory = Path.Combine(_config.Output, "history") },
                Dashboard = new() { RankPanelCollapsed = true } };
            var images = new BiliImageCache(new FixtureImages(), Path.Combine(_config.Output, "images"));
            ViewModel = new(settings, new SoakStore(), _client, new SoakInfo(), images);
            _window = new(ViewModel) { Title = "内存模拟 · " + _config.Name };
            _window.Activate();
            _window.OpenOverlay();
            if (_config.View == "overlay") _window.HideToTray();
            _process = Process.GetCurrentProcess();
            _previousCpu = _process.TotalProcessorTime.TotalSeconds;
            _elapsed.Start();
            PerformanceCounters.Enabled = true;
            _sampling = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _sampling.Interval = TimeSpan.FromSeconds(5); _sampling.Tick += (_, _) => Sample(); _sampling.Start();
            _log.WriteLine(JsonSerializer.Serialize(new { kind = "header", config = _config, pid = Environment.ProcessId,
                runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount,
                assembly = typeof(App).Assembly.GetName().Name, dpi = _window.Content.XamlRoot?.RasterizationScale,
                timestamp = DateTimeOffset.UtcNow, simulatedImages = true, realArchive = true, realRendering = true,
                note = "No external network, browser login, user settings or user history. Extra rank-panel HTTP disabled." }));
            Sample();
            await ViewModel.StartAsync(settings.RoomId);
            await Task.Delay(TimeSpan.FromSeconds(_config.WarmupSeconds));
            _phase = "traffic";
            await Task.Run(async () =>
            {
                var start = Stopwatch.GetTimestamp();
                using var cadence = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                long sent = 0; var lastProfile = -1;
                while (Stopwatch.GetElapsedTime(start).TotalSeconds < _config.Seconds && await cadence.WaitForNextTickAsync(_stop.Token))
                {
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    var expected = (long)(elapsed * _config.Rate);
                    // Bounded catch-up, to avoid unlimited synthetic backlog if rendering stalls.
                    var count = Math.Min(5000, expected - sent);
                    for (var i = 0; i < count; i++) Emit(++sent);
                    if ((int)elapsed / 30 != lastProfile) { lastProfile = (int)elapsed / 30; EmitRoom(settings.RoomId); }
                }
            });
            // Let the normal display batch finish before taking retention checks.
            await Task.Delay(TimeSpan.FromSeconds(2));
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
    private void Emit(long sequence)
    {
        Interlocked.Increment(ref _produced);
        var uid = sequence % _config.Users + 1;
        var type = sequence % 401 == 0 ? LiveEventType.Guard : sequence % 100 == 0 ? LiveEventType.SuperChat
            : sequence % 17 == 0 ? LiveEventType.Gift : sequence % 7 == 0 ? LiveEventType.Enter : LiveEventType.Danmaku;
        var emote = sequence % 3 == 0;
        var appearance = new LiveMessageAppearance { AvatarUrl = $"https://i0.hdslb.com/soak/avatar/{uid}.png", MedalName = "测试团",
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
        _phase = "finished"; Sample(); _finishing = true; _stop.Cancel(); _sampling?.Stop();
        _window?.AllowClose(); await ViewModel.DisposeAsync(); _window?.Close();
        _log?.Dispose(); _process?.Dispose();
        File.WriteAllText(Path.Combine(_config.Output, "completed.json"), JsonSerializer.Serialize(new { seconds = _elapsed.Elapsed.TotalSeconds, produced = _produced }));
        Exit();
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
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
internal sealed record SoakConfig(string Output, string Name = "baseline", int Seconds = 180, int Rate = 50, int Users = 20000, int WarmupSeconds = 10, string View = "both");
internal sealed class SoakStore : ISettingsStore
{
    public string Path => "isolated-memory-only";
    public AppSettings Load() => new();
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
internal sealed class SoakClient : ILiveRoomClient
{
    public event EventHandler<LiveEvent>? EventReceived;
    public event EventHandler<string>? StatusChanged;
    public bool IsRunning { get; private set; }
    public Task StartAsync(int roomId, CookieOptions cookies, CancellationToken cancellationToken = default)
    { IsRunning = true; StatusChanged?.Invoke(this, "本地模拟（无网络）"); return Task.CompletedTask; }
    public Task StopAsync() { IsRunning = false; StatusChanged?.Invoke(this, "已断开连接"); return Task.CompletedTask; }
    public void Emit(LiveEvent value) { if (IsRunning) EventReceived?.Invoke(this, value); }
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
