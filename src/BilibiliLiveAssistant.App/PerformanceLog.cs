using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace BilibiliLiveAssistant.App;

internal sealed record PerformanceLogState(bool Running = false, long Samples = 0, string Error = "");

/// <summary>Opt-in, numeric-only sampling. No per-event I/O, forced GC, or concurrent sampler.</summary>
internal sealed class PerformanceLog(DispatcherQueue dispatcher, Func<object> snapshot) : IAsyncDisposable
{
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BilibiliLiveAssistant", "diagnostics");
    private const int FileLimit = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private PerformanceLogState _state = new();
    private UiSample? _ui;
    private bool _disposed;
    private int _scenario;
    internal int Scenario { get => Volatile.Read(ref _scenario); set => Volatile.Write(ref _scenario, Math.Clamp(value, 0, 5)); }
    internal PerformanceLogState State => Volatile.Read(ref _state);
    internal event Action? Changed; // UI thread only
    private sealed record UiSample(long At, double DelayMs, object Data, object Timings);

    internal void Start()
    {
        if (_disposed || _worker is { IsCompleted: false }) return;
        _stop?.Dispose(); _stop = new();
        PerformanceCounters.Reset(); PerformanceCounters.Enabled = true;
        _ui = null; Volatile.Write(ref _state, new(true));
        _worker = Task.Run(() => RunAsync(_stop.Token));
        Changed?.Invoke();
    }

    private async Task RunAsync(CancellationToken token)
    {
        StreamWriter? writer = null;
        TaskCompletionSource? pulse = null;
        var session = Guid.NewGuid().ToString("N");
        var start = Stopwatch.GetTimestamp();
        var previous = start;
        var allocated = GC.GetTotalAllocatedBytes(false);
        long samples = 0, length = 0, lastUiAt = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            Directory.CreateDirectory(DirectoryPath);
            writer = OpenFile();
            await HeaderAsync();
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                // One outstanding low-priority UI pulse, even if the UI is blocked for minutes.
                if (pulse is null || pulse.Task.IsCompleted)
                {
                    var queuedAt = Stopwatch.GetTimestamp();
                    var completion = pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    {
                        try
                        {
                            if (token.IsCancellationRequested || _disposed) return;
                            var at = Stopwatch.GetTimestamp();
                            Volatile.Write(ref _ui, new(at, Stopwatch.GetElapsedTime(queuedAt, at).TotalMilliseconds,
                                snapshot(), PerformanceCounters.Drain()));
                            Changed?.Invoke();
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            Volatile.Write(ref _state, State with { Error = "Snapshot:" + ex.GetType().Name });
                            _stop?.Cancel();
                        }
                        finally { completion.TrySetResult(); }
                    })) break;
                }
                // Align the UI snapshot with process counters instead of always
                // correlating CPU with the previous five-second UI interval.
                // A blocked UI never delays process sampling indefinitely.
                if (pulse is not null && !pulse.Task.IsCompleted)
                {
                    try { await pulse.Task.WaitAsync(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                process.Refresh();
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(previous, now).TotalSeconds;
                var nextCpu = process.TotalProcessorTime;
                var nextAllocated = GC.GetTotalAllocatedBytes(false);
                var gc = GC.GetGCMemoryInfo(); // Latest GC observation, not a forced collection.
                var native = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
                var hasNativeMemory = GetProcessMemoryInfo(process.Handle, ref native, native.Size);
                var generations = new object[gc.GenerationInfo.Length];
                for (var i = 0; i < generations.Length; i++)
                {
                    var generation = gc.GenerationInfo[i];
                    generations[i] = new { generation = i, sizeAfterBytes = generation.SizeAfterBytes,
                        fragmentedAfterBytes = generation.FragmentationAfterBytes };
                }
                var ui = Volatile.Read(ref _ui);
                // Timings belong to this pulse, never repeat them while the UI is stalled.
                var fresh = ui is not null && ui.At != lastUiAt;
                if (fresh) lastUiAt = ui!.At;
                var line = JsonSerializer.Serialize(new
                {
                    kind = "sample", session, scenario = Scenario, utc = DateTimeOffset.UtcNow, uptimeSeconds = Stopwatch.GetElapsedTime(start, now).TotalSeconds,
                    intervalSeconds = elapsed, cpuPercent = (nextCpu - cpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100,
                    workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
                    privateWorkingSetBytes = hasNativeMemory ? (ulong?)native.PrivateWorkingSetSize : null,
                    managedBytes = GC.GetTotalMemory(false), gcHeapBytes = gc.HeapSizeBytes, gcFragmentedBytes = gc.FragmentedBytes,
                    gcCommittedBytes = gc.TotalCommittedBytes, gcGenerations = generations,
                    gcFinalizationPending = gc.FinalizationPendingCount,
                    allocatedBytes = Math.Max(0, nextAllocated - allocated), gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                    threads = process.Threads.Count, handles = process.HandleCount,
                    gdiObjects = GetGuiResources(process.Handle, 0), userObjects = GetGuiResources(process.Handle, 1),
                    uiAgeMs = ui is null ? (double?)null : Stopwatch.GetElapsedTime(ui.At, now).TotalMilliseconds,
                    uiDelayMs = ui?.DelayMs, uiFresh = fresh, ui = ui?.Data, timings = fresh ? ui?.Timings : null,
                }, JsonOptions);
                if (length + Encoding.UTF8.GetByteCount(line) + 1 > FileLimit)
                {
                    await writer.DisposeAsync().ConfigureAwait(false); writer = null;
                    writer = OpenFile(); length = 0; await HeaderAsync();
                }
                await WriteAsync(line);
                previous = now; cpu = nextCpu; allocated = nextAllocated;
                Volatile.Write(ref _state, new(true, ++samples));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Volatile.Write(ref _state, new(false, samples, ex.GetType().Name)); }
        finally
        {
            if (writer is not null)
            {
                try { await writer.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Volatile.Write(ref _state, new(false, samples, ex.GetType().Name)); }
            }
            PerformanceCounters.Enabled = false;
            var prior = State; Volatile.Write(ref _state, prior with { Running = false });
            dispatcher.TryEnqueue(() => { if (!_disposed) Changed?.Invoke(); });
        }

        async Task WriteAsync(string line)
        {
            await writer!.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
            length += Encoding.UTF8.GetByteCount(line) + 1;
        }
        Task HeaderAsync() => WriteAsync(JsonSerializer.Serialize(new { kind = "header", schema = 4, session, utc = DateTimeOffset.UtcNow,
            version = typeof(App).Assembly.GetName().Version?.ToString(),
            build = typeof(App).Assembly.GetCustomAttributes(false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
            logicalProcessors = Environment.ProcessorCount, sampleSeconds = 5, processScope = "app-only",
            cpuScale = "processCpuSeconds/wallSeconds/logicalProcessors*100", runtime = Environment.Version.ToString() }, JsonOptions));
    }

    private static string LogPath(int index) => Path.Combine(DirectoryPath, $"performance.{index}.jsonl");
    private static StreamWriter OpenFile()
    {
        // Preserve the previous run as well as rollovers. At most four 2 MiB files.
        RotateFiles();
        return new(new FileStream(LogPath(0), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.Asynchronous),
            new UTF8Encoding(false)) { NewLine = "\n" };
    }
    private static void RotateFiles()
    {
        if (File.Exists(LogPath(3))) File.Delete(LogPath(3));
        for (var i = 2; i >= 0; i--) if (File.Exists(LogPath(i))) File.Move(LogPath(i), LogPath(i + 1));
    }
    internal async Task StopAsync()
    {
        _stop?.Cancel(); PerformanceCounters.Enabled = false;
        Volatile.Write(ref _ui, null);
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { Volatile.Write(ref _state, State with { Error = "StopTimeout" }); }
        }
        Changed?.Invoke();
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; await StopAsync();
        if (_worker is null || _worker.IsCompleted) _stop?.Dispose();
        Changed = null;
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
    // PROCESS_MEMORY_COUNTERS_EX2 from the Windows SDK. Older Windows may not
    // support this size: keep the metric null rather than substituting commit.
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
