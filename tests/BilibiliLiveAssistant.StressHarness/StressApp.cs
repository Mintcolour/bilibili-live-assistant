using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using BilibiliLiveAssistant.Core;
using WinRT.Interop;

namespace BilibiliLiveAssistant.App;

#if !TRAY_SMOKE
public partial class App : Application
{
    public static LiveAssistantViewModel ViewModel {get; private set;} = null!;
    private readonly FakeClient _client = new();
    private MainWindow _window = null!;
    private OverlayWindow _overlay = null!;
    private DispatcherQueueTimer _sampling = null!;
    private readonly Stopwatch _clock = new();
    private readonly List<object> _samples = new();
    private Process _process = null!;
    private double _lastCpu, _lastTime;
    private long _produced;
    private volatile bool _done;

    public App()
    {
        InitializeComponent();
        ViewModel = new LiveAssistantViewModel(new AppSettings {RoomId=1}, new FakeStore(), _client, new FakeInfo());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(ViewModel);
        _window.Title = "高流量弹幕对照测试";
        _window.Activate();
        var appWindow=AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(_window)));
        appWindow.Resize(new Windows.Graphics.SizeInt32(1100,720));
        _window.OpenOverlay();
        _overlay=(OverlayWindow)typeof(MainWindow).GetField("_overlay",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(_window)!;
        _overlay.Bounds=new System.Drawing.Rectangle(0,0,1920,360);
        _process=Process.GetCurrentProcess();
        _lastCpu=_process.TotalProcessorTime.TotalSeconds;
        _clock.Start();
        _sampling=DispatcherQueue.GetForCurrentThread().CreateTimer();
        _sampling.Interval=TimeSpan.FromSeconds(1);
        _sampling.Tick+=Sample;
        _sampling.Start();
        var dispatcher=DispatcherQueue.GetForCurrentThread();
        _=Task.Run(async () =>
        {
            var burstDone=false;
            var clickStep=0;
            using var cadence=new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while (await cadence.WaitForNextTickAsync() && !_done)
            {
                var seconds=_clock.Elapsed.TotalSeconds;
                if(seconds>=5 && seconds<15)
                {
                    if(++clickStep % 5 == 0) dispatcher.TryEnqueue(() =>
                    {
                        _window.OpenOverlay();
                        ViewModel.SendTestDanmaku();
                        Interlocked.Increment(ref _produced);
                    });
                }
                else if(seconds>=15 && seconds<45)
                {
                    var count=seconds<30 ? 2 : 20;
                    for(var n=0;n<count;n++) Emit();
                }
                else if(seconds>=45 && !burstDone)
                {
                    burstDone=true;
                    for(var n=0;n<3000;n++) Emit();
                }
            }
        });
    }

    private void Emit()
    {
        var sequence=Interlocked.Increment(ref _produced);
        var type=sequence%100==0 ? LiveEventType.SuperChat : sequence%50==0 ? LiveEventType.Gift : LiveEventType.Danmaku;
        _client.Emit(new LiveEvent(type,$"用户{sequence%200}",$"弹幕压力模拟消息 #{sequence}",type==LiveEventType.Danmaku ? "#FFFFFF":"#FFD700"));
    }

    private void Sample(DispatcherQueueTimer sender,object args)
    {
        _process.Refresh();
        var seconds=_clock.Elapsed.TotalSeconds;
        var cpu=_process.TotalProcessorTime.TotalSeconds;
        var midpoint=(_lastTime+seconds)/2;
        var phase=midpoint<5 ? "warmup" : midpoint<15 ? "clicks_10ps" : midpoint<30 ? "messages_100ps" : midpoint<45 ? "messages_1000ps" : midpoint<50 ? "burst_3000" : midpoint<70 ? "recovery" : "idle_after";
        _samples.Add(new {
            Phase=phase,Elapsed=seconds,IntervalSeconds=seconds-_lastTime,
            CpuPercent=100*(cpu-_lastCpu)/(seconds-_lastTime)/Environment.ProcessorCount,
            Produced=Interlocked.Read(ref _produced),HistoryCount=ViewModel.Events.Count,
            ActiveSprites=((System.Collections.ICollection)typeof(OverlayWindow).GetField("_items",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(_overlay)!).Count,
            WorkingSetMB=_process.WorkingSet64/1048576d,PrivateMemoryMB=_process.PrivateMemorySize64/1048576d,
            HandleCount=_process.HandleCount,Gen0=GC.CollectionCount(0),Gen1=GC.CollectionCount(1),Gen2=GC.CollectionCount(2),
            BufferedDropped=typeof(LiveAssistantViewModel).GetProperty("BufferedDroppedCount")?.GetValue(ViewModel),
            OverlayDropped=typeof(LiveAssistantViewModel).GetProperty("OverlayDroppedCount")?.GetValue(ViewModel),
            PendingUi=ReadBufferCount(ViewModel,"_pendingEvents"),
            PendingOverlay=ReadBufferCount(_overlay,"_pendingItems"),
            AnimationRunning=((System.Windows.Forms.Timer)typeof(OverlayWindow).GetField("_timer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(_overlay)!).Enabled,
        });
        _lastCpu=cpu; _lastTime=seconds;
        if(seconds<80) return;
        _done=true; _sampling.Stop();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"stress-results.json"),JsonSerializer.Serialize(new {LogicalProcessors=Environment.ProcessorCount,Samples=_samples},new JsonSerializerOptions {WriteIndented=true}));
        _window.AllowClose();
        _window.Close();
        Exit();
    }

    private static int? ReadBufferCount(object owner,string name)
    {
        var buffer=owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(owner);
        return buffer is null ? null : (int?)buffer.GetType().GetProperty("Count")?.GetValue(buffer);
    }
}
#endif

internal sealed class FakeClient : ILiveRoomClient
{
    public event EventHandler<LiveEvent>? EventReceived;
    public event EventHandler<string>? StatusChanged {add{} remove{}}
    public bool IsRunning=>false;
    public Task StartAsync(int roomId,CookieOptions cookies,CancellationToken cancellationToken=default)=>Task.CompletedTask;
    public Task StopAsync()=>Task.CompletedTask;
    public void Emit(LiveEvent value)=>EventReceived?.Invoke(this,value);
}
internal sealed class FakeInfo : ILiveInfoService
{
    public event EventHandler<LiveEvent>? EventReceived {add{} remove{}}
    public Task StartAsync(int roomId,CookieOptions cookies,int intervalSeconds,CancellationToken cancellationToken=default)=>Task.CompletedTask;
    public Task StopAsync()=>Task.CompletedTask;
}
internal sealed class FakeStore : ISettingsStore
{
    public string Path=>"unused";
    public AppSettings Load()=>new();
    public Task SaveAsync(AppSettings settings,CancellationToken cancellationToken=default)=>Task.CompletedTask;
}
