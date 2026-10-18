using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BilibiliLiveAssistant.App;

// Observations are triggered by existing work, never an idle polling timer.
// This controls reclaimable resources, not the OS/WinUI process-memory total.
internal sealed class ProcessResourceBudget : IDisposable
{
    private const long Limit = 160L * 1024 * 1024;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly UiImageSourceService _sources;
    private readonly BiliImageCache _images;
    private long _nextCheck, _recoverySince, _privateWorkingSet, _checks, _failures, _changes;
    private int _level;
    private bool _disposed;
    internal ProcessResourceBudget(BiliImageCache images)
    { _images = images; _sources = images.Sources; _sources.ResourceActivity = Observe; }

    internal void Observe()
    {
        if (_disposed) return;
        var now = Stopwatch.GetTimestamp();
        if (now < _nextCheck) return;
        // Set first: trimming sources can itself release other image resources.
        _nextCheck = now + Stopwatch.Frequency;
        var memory = new Counters { Size = (uint)Marshal.SizeOf<Counters>() };
        if (!GetProcessMemoryInfo(_process.Handle, ref memory, memory.Size)) { _failures++; return; }
        _checks++; _privateWorkingSet = checked((long)memory.PrivateWorkingSet);
        var requested = _privateWorkingSet >= Limit * 95 / 100 ? 3 : _privateWorkingSet >= Limit * 90 / 100 ? 2
            : _privateWorkingSet >= Limit * 80 / 100 ? 1 : 0;
        if (requested > _level) { _recoverySince = 0; Apply(requested); return; }
        var recovery = _level switch { 3 => Limit * 90 / 100, 2 => Limit * 85 / 100, 1 => Limit * 75 / 100, _ => 0 };
        if (_level == 0 || _privateWorkingSet >= recovery) { _recoverySince = 0; return; }
        if (_recoverySince == 0) _recoverySince = now;
        else if (Stopwatch.GetElapsedTime(_recoverySince, now).TotalSeconds >= 10)
        { _recoverySince = 0; Apply(_level - 1); }
    }
    private void Apply(int level)
    {
        _level = level; _changes++;
        // Retain a small idle working set even at high pressure: row rebinding
        // transfers the same images between visible slots in one UI batch.
        var mib = level switch { 0 => 8, 1 => 4, 2 => 2, _ => 1 };
        _sources.SetIdleBudget(mib * 1024L * 1024, level switch { 0 => 256, 1 => 128, 2 => 64, _ => 32 });
        // Reclaim cached pixels as well as idle native display sources, leaving
        // the original 24 MiB ceiling at normal pressure. No quality reduction,
        // consumer cancellation or eager reload when the budget recovers.
        _images.SetMemoryBudget((level switch { 0 => 24L, 1 => 12L, 2 => 8L, _ => 4L }) * 1024 * 1024);
        ImageDecodeAdmission.SerializeForPressure = level >= 2;
        UiPixelSource.SerializeForPressure = level >= 2;
    }
    internal object Snapshot() => new { limitBytes = Limit, level = _level, observedPrivateWorkingSetBytes = _privateWorkingSet,
        checks = _checks, failures = _failures, changes = _changes, sampling = "activity-at-most-1hz", nativeTotalIsNotBounded = true };
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _sources.ResourceActivity = null;
        ImageDecodeAdmission.SerializeForPressure = false;
        UiPixelSource.SerializeForPressure = false;
        _process.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint Size, PageFaults;
        public nuint PeakWorkingSet, WorkingSet, PeakPaged, Paged, PeakNonPaged, NonPaged,
            Pagefile, PeakPagefile, PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommit;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(nint process, ref Counters counters, uint size);
}
