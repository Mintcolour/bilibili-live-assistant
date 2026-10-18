using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

// Offline only. No target process access, heap configuration or symbol download.
if (args.Length is < 4 or > 5 || !int.TryParse(args[1], out var targetPid) || targetPid <= 0)
{
    Console.Error.WriteLine("NativeHeapReader trace.etl pid modules.json output.json [snapshot-seconds,...]");
    return 1;
}
var points = (args.Length == 5 ? args[4] : "1,2,4,6,8").Split(',')
    .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
if (points.Any(s => !double.IsFinite(s) || s <= 0) || points.Length > 256)
    throw new ArgumentException("Snapshot times must be finite, positive; at most 256 points.");
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[3]);
if (new[] { input, Path.GetFullPath(args[2]) }.Contains(output, StringComparer.OrdinalIgnoreCase) || File.Exists(output))
    throw new ArgumentException("Choose a new output file; inputs and existing evidence must not be overwritten.");
using var moduleDocument = JsonDocument.Parse(File.ReadAllText(args[2]));
var modules = moduleDocument.RootElement.GetProperty("modules").EnumerateArray().Select(m => new Module(
    m.GetProperty("name").GetString()!, m.GetProperty("address").GetUInt64(), m.GetProperty("size").GetUInt32())).ToArray();
using var log = TraceLog.OpenOrConvert(input, new TraceLogOptions { KeepAllEvents = true });
using var source = log.Events.GetSource();
var heap = new HeapTraceProviderTraceEventParser(source);
var live = new Dictionary<(ulong Heap, ulong Address), Allocation>();
var snapshots = new List<object>();
var frames = new Dictionary<int, string[]>();
var releasedAges = new long[6];
var releasedBytes = new long[6];
var nextPoint = 0;
long rawAllocations = 0, rawFrees = 0, rawReallocations = 0, rawReportedBytes = 0;
long nestedReallocations = 0, unexplainedAddressReplacements = 0, unpairedFrees = 0, unpairedReallocationOld = 0;
long nestedOldStillPresent = 0, destroys = 0;
double lastMs = 0;

string[] Frames(int index)
{
    if (frames.TryGetValue(index, out var cached)) return cached;
    if (index < 0) return ["<no captured stack>"];
    var result = new List<string>();
    for (var stack = log.CallStacks[(CallStackIndex)index]; stack is not null && result.Count < 128; stack = stack.Caller)
    {
        var address = stack.CodeAddress.Address;
        var module = modules.FirstOrDefault(m => address >= m.Address && address - m.Address < m.Size);
        result.Add(module is null ? $"0x{address:X}" : $"{module.Name}+0x{address - module.Address:X}");
    }
    return frames[index] = result.ToArray();
}
void Released(Allocation value, double atMs)
{
    var age = Math.Max(0, atMs - value.AtMs);
    var bucket = age <= 1 ? 0 : age <= 10 ? 1 : age <= 100 ? 2 : age <= 1000 ? 3 : age <= 5000 ? 4 : 5;
    releasedAges[bucket]++;
    releasedBytes[bucket] += value.Size;
}
object[] Top(IEnumerable<Allocation> values) => values.GroupBy(a => a.Stack).Select(g => new
{
    stack = g.Key, bytes = g.Sum(a => a.Size), count = g.Count(), oldestMs = g.Min(a => a.AtMs),
    newestMs = g.Max(a => a.AtMs),
}).OrderByDescending(g => g.bytes).Take(200).Select(g => (object)new
{ g.stack, g.bytes, g.count, g.oldestMs, g.newestMs, frames = Frames(g.stack) }).ToArray();
void Snapshot(double atMs)
{
    var top = Top(live.Values);
    var cohorts = new[] { 1000d, 3000d, 5000d, 30000d, 60000d }.Select(age => new
    {
        minimumAgeMs = age, count = live.Values.Count(a => atMs - a.AtMs >= age),
        bytes = live.Values.Where(a => atMs - a.AtMs >= age).Sum(a => a.Size),
        top = Top(live.Values.Where(a => atMs - a.AtMs >= age)),
    }).ToArray();
    snapshots.Add(new { atMs, liveCount = live.Count, liveBytes = live.Values.Sum(a => a.Size), cohorts, top });
    Console.WriteLine(FormattableString.Invariant($"{atMs / 1000:F3}s: {live.Count} observed live allocations, {live.Values.Sum(a => a.Size)} bytes"));
}
void Before(TraceEvent e)
{
    lastMs = Math.Max(lastMs, e.TimeStampRelativeMSec);
    while (nextPoint < points.Length && e.TimeStampRelativeMSec >= points[nextPoint] * 1000)
        Snapshot(points[nextPoint++] * 1000);
}
Allocation NewAllocation(long size, TraceEvent e, bool fromAlloc) =>
    new(size, e.TimeStampRelativeMSec, (int)log.GetCallStackIndexForEvent(e), e.ThreadID, fromAlloc);
heap.HeapTraceAlloc += e =>
{
    if (e.ProcessID != targetPid) return;
    Before(e); rawAllocations++; rawReportedBytes += e.AllocSize;
    var key = (e.HeapHandle, e.AllocAddress);
    if (live.ContainsKey(key)) unexplainedAddressReplacements++;
    live[key] = NewAllocation(e.AllocSize, e, true);
};
heap.HeapTraceFree += e =>
{
    if (e.ProcessID != targetPid) return;
    Before(e); rawFrees++;
    if (live.Remove((e.HeapHandle, e.FreeAddress), out var value)) Released(value, e.TimeStampRelativeMSec);
    else unpairedFrees++;
};
heap.HeapTraceReAlloc += e =>
{
    if (e.ProcessID != targetPid) return;
    Before(e); rawReallocations++; rawReportedBytes += e.NewAllocSize;
    var oldKey = (e.HeapHandle, e.OldAllocAddress);
    var key = (e.HeapHandle, e.NewAllocAddress);
    // Moving reallocation can emit inner Alloc(new), Free(old), then outer
    // ReAlloc(old,new). Keep one generation, not a phantom second allocation.
    var nested = oldKey != key && live.TryGetValue(key, out var existing) && existing.FromAlloc
        && existing.Size == e.NewAllocSize && existing.Thread == e.ThreadID
        && e.TimeStampRelativeMSec - existing.AtMs is >= 0 and < 1;
    var hadOld = live.Remove(oldKey, out var old);
    if (oldKey == key && hadOld)
    {
        // In-place resizing does not start a new address lifetime.
        live[key] = old! with { Size = e.NewAllocSize };
        return;
    }
    if (nested)
    {
        nestedReallocations++;
        if (hadOld) { nestedOldStillPresent++; Released(old!, e.TimeStampRelativeMSec); }
        return;
    }
    if (hadOld) Released(old!, e.TimeStampRelativeMSec); else unpairedReallocationOld++;
    if (live.ContainsKey(key)) unexplainedAddressReplacements++;
    live[key] = NewAllocation(e.NewAllocSize, e, false);
};
heap.HeapTraceDestroy += e =>
{
    if (e.ProcessID != targetPid) return;
    Before(e); destroys++;
    foreach (var key in live.Keys.Where(k => k.Heap == e.HeapHandle).ToArray())
    { Released(live[key], e.TimeStampRelativeMSec); live.Remove(key); }
};
source.Process(); Snapshot(lastMs);
var result = new
{
    schema = 1, targetPid, log.EventCount, log.EventsLost, log.HasCallStacks, log.Truncated, lastEventMs = lastMs,
    rawAllocations, rawFrees, rawReallocations, rawReportedBytes,
    nestedReallocations, nestedOldStillPresent, unexplainedAddressReplacements, unpairedFrees, unpairedReallocationOld, destroys,
    releasedLifetimes = new { rangesMs = new[] { "0-1", "1-10", "10-100", "100-1000", "1000-5000", ">5000" }, counts = releasedAges, bytes = releasedBytes },
    snapshots,
    note = "Offline PID-scoped heap evidence only. Pre-attachment allocations are absent. Raw byte totals include nested events and are not allocation throughput. Observed live objects may be valid caches, not leaks. Unexplained replacements or nested-old matches require review. EventsLost does not certify LostBuffers: also check xperf tracestats. Module offsets are not symbols. No process-wide memory total or stability verdict is inferred.",
};
using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(destination, result, new JsonSerializerOptions { WriteIndented = true });
return 0;

sealed record Allocation(long Size, double AtMs, int Stack, int Thread, bool FromAlloc);
sealed record Module(string Name, ulong Address, uint Size);
