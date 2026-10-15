using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Win32.SafeHandles;

namespace BilibiliLiveAssistant.App;

// One sleeping worker for all moving regions. Never calls drawing code off the UI thread.
internal sealed class OverlayFrameClock : IDisposable
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueHandler _deliverFrame;
    private readonly ManualResetEvent _shutdown = new(false);
    private readonly AutoResetEvent _changed = new(false);
    private readonly AutoResetEvent _completed = new(false);
    private readonly List<Subscription> _subscriptions = new(4);
    private readonly Subscription?[] _snapshot = new Subscription?[8];
    private Task? _worker;
    private int _activeCount;
    private int _frameRate = 60;
    private volatile bool _disposed;
    private volatile bool _workerEnded;
    private int _cleanedEvents;

    public OverlayFrameClock() => _deliverFrame = DeliverFrame;

    public void Configure(int requestedRate, string displayName)
    {
        if (_disposed) return;
        var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>() };
        var refreshRate = EnumDisplaySettings(displayName, -1, ref mode) && mode.Frequency > 1
            ? (int)Math.Clamp(mode.Frequency, 30u, 240u) : 60;
        var rate = requestedRate == 0 ? refreshRate : Math.Min(Math.Clamp(requestedRate, 30, 240), refreshRate);
        if (Interlocked.Exchange(ref _frameRate, rate) != rate) _changed.Set();
    }

    public bool IsRunning(Action<double> callback) => _subscriptions.Any(s => s.Callback == callback);

    public void Start(Action<double> callback)
    {
        if (_disposed || IsRunning(callback)) return;
        if (_subscriptions.Count == _snapshot.Length) throw new InvalidOperationException("滚动区域数量超出动画时钟上限。");
        _subscriptions.Add(new Subscription(callback));
        Volatile.Write(ref _activeCount, _subscriptions.Count);
        _worker ??= Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _changed.Set();
    }

    public void Stop(Action<double> callback)
    {
        if (_disposed) return;
        var subscription = _subscriptions.Find(s => s.Callback == callback);
        if (subscription is null) return;
        subscription.Active = false;
        _subscriptions.Remove(subscription);
        Volatile.Write(ref _activeCount, _subscriptions.Count);
        _changed.Set();
    }

    private void Run()
    {
        // High resolution is scoped to this waitable timer; no global timeBeginPeriod.
        using var deadline = NativeDeadline.Create();
        var idleWait = new WaitHandle[] { _shutdown, _changed };
        var frameWait = new WaitHandle[] { _shutdown, _changed, _completed };
        var dueWait = deadline is null ? null : new WaitHandle[] { _shutdown, _changed, deadline };
        double nextAt = 0;
        try
        {
            while (!_disposed)
            {
                if (Volatile.Read(ref _activeCount) == 0)
                {
                    nextAt = 0;
                    if (WaitHandle.WaitAny(idleWait) == 0) break;
                    continue;
                }
                var period = Stopwatch.Frequency / (double)Volatile.Read(ref _frameRate);
                var now = Stopwatch.GetTimestamp();
                if (nextAt == 0) nextAt = now;
                var remaining = (nextAt - now) / Stopwatch.Frequency;
                if (remaining > 0)
                {
                    if (deadline is not null && deadline.Arm(remaining))
                    {
                        var signal = WaitHandle.WaitAny(dueWait!);
                        if (signal == 0) break;
                        if (signal == 1) { nextAt = 0; continue; }
                    }
                    else
                    {
                        var signal = WaitHandle.WaitAny(idleWait, (int)Math.Clamp(Math.Ceiling(remaining * 1000), 1, 100));
                        if (signal == 0) break;
                        if (signal == 1) { nextAt = 0; continue; }
                    }
                }
                if (_disposed || Volatile.Read(ref _activeCount) == 0) continue;
                // DWM waits here, never on the UI thread. If unavailable, the deadline
                // still limits dispatch frequency and prevents a hot retry loop.
                _ = DwmFlush();
                if (_disposed || Volatile.Read(ref _activeCount) == 0) continue;
                now = Stopwatch.GetTimestamp();
                nextAt += period;
                if (nextAt < now - period) nextAt = now;
                _completed.Reset();
                if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, _deliverFrame)) break;
                // Only one outstanding callback. Changes wake this wait, but cannot
                // permit another dispatch until the previous callback has completed.
                while (!_disposed)
                {
                    var signal = WaitHandle.WaitAny(frameWait);
                    if (signal == 0) return;
                    if (signal == 2) break;
                    nextAt = 0;
                }
            }
        }
        finally
        {
            _workerEnded = true;
            if (_disposed) CleanupEvents();
        }
    }

    private void DeliverFrame()
    {
        try
        {
            if (_disposed) return;
            var now = Stopwatch.GetTimestamp();
            var count = _subscriptions.Count;
            _subscriptions.CopyTo(_snapshot!);
            for (var i = 0; i < count; i++)
            {
                if (_disposed) break;
                var subscription = _snapshot[i]!;
                if (!subscription.Active) continue;
                var elapsed = Math.Min((now - subscription.LastAt) / (double)Stopwatch.Frequency, .1);
                subscription.LastAt = now;
                subscription.Callback(elapsed);
            }
        }
        finally
        {
            Array.Clear(_snapshot);
            // A queued callback may run after disposal and worker exit.
            if (!_disposed) _completed.Set();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var subscription in _subscriptions) subscription.Active = false;
        _subscriptions.Clear(); Array.Clear(_snapshot);
        Volatile.Write(ref _activeCount, 0);
        _shutdown.Set();
        _disposed = true;
        // Do not wait for DWM from the UI thread. The worker owns event cleanup.
        if (_worker is null || _workerEnded) CleanupEvents();
    }

    private void CleanupEvents()
    {
        if (Interlocked.Exchange(ref _cleanedEvents, 1) != 0) return;
        _shutdown.Dispose(); _changed.Dispose(); _completed.Dispose();
    }

    private sealed class Subscription
    {
        public readonly Action<double> Callback;
        public long LastAt = Stopwatch.GetTimestamp();
        public bool Active = true;
        public Subscription(Action<double> callback) => Callback = callback;
    }

    private sealed class NativeDeadline : WaitHandle
    {
        private NativeDeadline(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, true);
        public static NativeDeadline? Create()
        {
            var handle = CreateWaitableTimerEx(0, null, 2, 0x001F0003);
            if (handle == 0) handle = CreateWaitableTimerEx(0, null, 0, 0x001F0003);
            return handle == 0 ? null : new NativeDeadline(handle);
        }
        public bool Arm(double seconds)
        {
            var due = -(long)Math.Max(1, Math.Ceiling(seconds * 10_000_000));
            return SetWaitableTimer(SafeWaitHandle, ref due, 0, 0, 0, false);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int X, Y;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPixel, Width, Height, Flags, Frequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode settings);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWaitableTimerExW")]
    private static extern nint CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, nint completion, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
