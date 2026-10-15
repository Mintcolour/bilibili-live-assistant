param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$runPath = (Resolve-Path -LiteralPath $Path).Path
$rows = @(Get-Content -LiteralPath (Join-Path $runPath 'samples.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
$header = $rows | Where-Object kind -eq header | Select-Object -First 1
$samples = @($rows | Where-Object kind -eq sample)
if ($samples.Count -eq 0) { throw '没有可分析的采样' }
$traffic = @($samples | Where-Object phase -eq traffic)
$completedPath = Join-Path $runPath 'completed.json'
$completed = if (Test-Path -LiteralPath $completedPath) { Get-Content -LiteralPath $completedPath -Raw | ConvertFrom-Json } else { $null }
function Get-MemorySummary([string]$Field) {
    $values = @($samples | ForEach-Object { $_.$Field } | Where-Object { $null -ne $_ })
    if ($values.Count -eq 0) { return $null }
    $crossing = $samples | Where-Object { $_.$Field -ge 350MB } | Select-Object -First 1
    [ordered]@{
        peakBytes = ($values | Measure-Object -Maximum).Maximum
        peakMiB = [math]::Round(($values | Measure-Object -Maximum).Maximum / 1MB, 2)
        lastMiB = [math]::Round($values[-1] / 1MB, 2)
        below180DecimalMB = ($values | Measure-Object -Maximum).Maximum -lt 180000000
        below180MiB = ($values | Measure-Object -Maximum).Maximum -lt 180MB
        first350MiBSeconds = if ($crossing) { $crossing.seconds } else { $null }
        first350MiBMessages = if ($crossing) { $crossing.produced } else { $null }
    }
}
$cpu = @($traffic.cpuPercent | Sort-Object)
$last = $samples[-1]
$steady = @($traffic | Where-Object seconds -ge ([math]::Max(60, $last.seconds - 900)))
$allocationRate = if ($steady.Count -gt 1) { ($steady[-1].allocatedBytes - $steady[0].allocatedBytes) / ($steady[-1].seconds - $steady[0].seconds) / 1MB } else { $null }
$frameIntervals = ($traffic.timing.frameCadence.intervals | Measure-Object -Sum).Sum
$lateFrames = ($traffic.timing.frameCadence.lateFrames | Measure-Object -Sum).Sum
[ordered]@{
    name = $header.config.Name
    configuration = $header.config
    finished = $null -ne $completed
    elapsedSeconds = $last.seconds
    produced = $last.produced
    completedTrafficSeconds = if ($completed) { $header.config.Seconds } else { $null }
    twoHourTrafficCompleted = $null -ne $completed -and $header.config.Seconds -ge 7200 -and $completed.seconds -ge 7200 -and $completed.produced -ge 7200 * $header.config.Rate
    privateCommit = Get-MemorySummary 'privateBytes'
    totalWorkingSet = Get-MemorySummary 'workingSetBytes'
    privateWorkingSet = Get-MemorySummary 'privateWorkingSetBytes'
    cpuP95Percent = if ($cpu.Count) { $cpu[[math]::Min($cpu.Count-1, [math]::Ceiling($cpu.Count * 0.95)-1)] } else { $null }
    allocationMiBPerSecond = $allocationRate
    frameIntervals = $frameIntervals
    lateFrameFraction = if ($frameIntervals) { $lateFrames / $frameIntervals } else { $null }
    displayBufferDropped = $last.ui.bufferDropped
    overlayDropped = $last.ui.overlayDropped
    archiveDropped = $last.ui.history.dropped
    archiveWriteFailures = $last.ui.history.writeFailures
    archivedEvents = $last.ui.history.writtenEvents
    archivePending = $last.ui.history.pending
    diskScans = $last.ui.images.diskScans
    assertions = $rows | Where-Object kind -eq assertions | Select-Object -Last 1
    note = 'Sampling every ~5s; peaks are observed samples, not continuous maxima. MiB=1048576 bytes. Overlay expiry/queue drops are separately reported; no forced GC or working-set trimming.'
} | ConvertTo-Json -Depth 8
