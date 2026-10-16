param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$ExpectedPath,
    [Parameter(Mandatory)][string]$Output,
    [ValidateRange(1,14400)][int]$Seconds = 3900,
    [ValidateRange(100,1000)][int]$IntervalMilliseconds = 250,
    [ValidateRange(1,1024)][int]$BudgetMiB = 160
)
$ErrorActionPreference = 'Stop'
$expected = (Resolve-Path -LiteralPath $ExpectedPath).Path
$target = Get-Process -Id $ProcessId -ErrorAction Stop
if (![string]::Equals($target.Path, $expected, [StringComparison]::OrdinalIgnoreCase)) { throw '目标进程路径不匹配' }
if (Test-Path -LiteralPath $Output) { throw '采样文件已存在，拒绝覆盖' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BudgetProcessMemory {
    [StructLayout(LayoutKind.Sequential)] public struct Counters {
        public uint Size, Faults;
        public UIntPtr PeakWorkingSet, WorkingSet, PeakPaged, Paged, PeakNonPaged, NonPaged,
            Pagefile, PeakPagefile, PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommit;
    }
    [DllImport("psapi.dll", SetLastError=true)] public static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);
    public static Counters Read(IntPtr handle) {
        var value = new Counters(); value.Size = (uint)Marshal.SizeOf<Counters>();
        if (!GetProcessMemoryInfo(handle, ref value, value.Size)) throw new System.ComponentModel.Win32Exception();
        return value;
    }
}
'@
$writer = [IO.StreamWriter]::new([IO.FileStream]::new($Output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read))
$watch = [Diagnostics.Stopwatch]::StartNew()
$meter = [Diagnostics.Process]::GetCurrentProcess()
$meterCpu = $meter.TotalProcessorTime.TotalSeconds
$previousCpu = $target.TotalProcessorTime.TotalSeconds
$previousSeconds = 0.0
$peak = 0L
$violations = 0
$samples = 0
try {
    $writer.WriteLine((@{kind='header'; pid=$ProcessId; utc=[DateTimeOffset]::UtcNow; processStartedUtc=$target.StartTime.ToUniversalTime(); intervalMs=$IntervalMilliseconds; budgetBytes=($BudgetMiB * 1MB); processors=[Environment]::ProcessorCount; note='External process sampling; no GC or working-set trimming; finite sampling cannot prove no inter-sample peak.'} | ConvertTo-Json -Compress))
    $writer.Flush()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds -and !$target.HasExited) {
        $memory = [BudgetProcessMemory]::Read($target.Handle)
        $elapsed = $watch.Elapsed.TotalSeconds
        $cpu = $target.TotalProcessorTime.TotalSeconds
        $privateWs = $memory.PrivateWorkingSet.ToUInt64()
        $peak = [Math]::Max($peak, [long]$privateWs)
        if ($privateWs -gt ($BudgetMiB * 1MB)) { $violations++ }
        $writer.WriteLine((@{kind='sample'; seconds=$elapsed; privateWorkingSetBytes=$privateWs; privateBytes=$memory.PrivateUsage.ToUInt64(); workingSetBytes=$memory.WorkingSet.ToUInt64(); cpuPercent=(100 * ($cpu - $previousCpu) / [Math]::Max(0.001, $elapsed - $previousSeconds) / [Environment]::ProcessorCount)} | ConvertTo-Json -Compress))
        $samples++
        if ($samples % 20 -eq 0) { $writer.Flush() }
        $previousCpu = $cpu; $previousSeconds = $elapsed
        [Threading.Thread]::Sleep($IntervalMilliseconds)
    }
}
finally {
    $writer.Flush(); $writer.Dispose()
    @{samples=$samples; peakPrivateWorkingSetBytes=$peak; overBudgetSamples=$violations; elapsedSeconds=$watch.Elapsed.TotalSeconds; meterCpuSeconds=($meter.TotalProcessorTime.TotalSeconds - $meterCpu)} |
        ConvertTo-Json | Set-Content -LiteralPath ($Output + '.summary.json') -Encoding utf8
    $target.Dispose(); $meter.Dispose()
}
