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
$assertions = $rows | Where-Object kind -eq assertions | Select-Object -Last 1
$eligible = $header.config.View -eq 'both' -and $header.config.Images -eq 'unique' -and !$header.config.Snapshot -and
    $header.config.AttachImages -ne $false -and $header.config.CreateSources -ne $false -and !$header.config.DrawingDecode -and !$header.config.ScrollProbe
$retentionPassed = $null -ne $assertions -and $assertions.stopRetainsContent -and $assertions.sameRoomRetainsContent -and
    $assertions.offlineRetainsContent -and $assertions.switchClearsContent
$buildVerified = $null -ne $header.build -and !$header.build.alternateSource
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
$completeRenderingRun = $eligible -and $buildVerified -and $retentionPassed -and $null -ne $completed -and
    $header.config.Seconds -ge 7200 -and $completed.seconds -ge 7200 -and $completed.produced -ge 7200 * $header.config.Rate
$privateWorkingSetSummary = Get-MemorySummary 'privateWorkingSetBytes'
function Get-TrafficWindow([object[]]$Window) {
    if ($Window.Count -lt 2) { return $null }
    $meanSeconds = ($Window.seconds | Measure-Object -Average).Average
    $denominator = 0.0
    foreach ($row in $Window) { $denominator += [math]::Pow($row.seconds - $meanSeconds, 2) }
    $slopes = [ordered]@{}
    foreach ($field in @('privateBytes', 'privateWorkingSetBytes', 'managedBytes')) {
        if (@($Window | Where-Object { $null -eq $_.$field }).Count) { $slopes[$field] = $null; continue }
        $meanBytes = ($Window | ForEach-Object { $_.$field } | Measure-Object -Average).Average
        $numerator = 0.0
        foreach ($row in $Window) { $numerator += ($row.seconds - $meanSeconds) * ($row.$field - $meanBytes) }
        $slopes[$field] = if ($denominator -gt 0) { [math]::Round(60 * $numerator / $denominator / 1MB, 4) } else { $null }
    }
    [ordered]@{ fromSeconds = $Window[0].seconds; toSeconds = $Window[-1].seconds; samples = $Window.Count; slopeMiBPerMinute = $slopes }
}
$trafficWindows = @(for ($startMinute = 0; $startMinute * 60 -lt $last.seconds; $startMinute += 15) {
    $window = @($traffic | Where-Object { $_.seconds -ge $startMinute * 60 -and $_.seconds -lt ($startMinute + 15) * 60 })
    if ($window.Count -gt 1) { Get-TrafficWindow $window }
})
[ordered]@{
    name = $header.config.Name
    configuration = $header.config
    winUiVersion = $header.winUiVersion
    build = $header.build
    runtimeBuildVerified = $buildVerified
    renderingAcceptanceEligible = $eligible
    retentionChecksPassed = $retentionPassed
    finished = $null -ne $completed
    elapsedSeconds = $last.seconds
    produced = $last.produced
    completedTrafficSeconds = if ($completed) { $header.config.Seconds } else { $null }
    twoHourTrafficCompleted = $null -ne $completed -and $header.config.Seconds -ge 7200 -and $completed.seconds -ge 7200 -and $completed.produced -ge 7200 * $header.config.Rate
    twoHourRenderingRunCompleted = $completeRenderingRun
    taskManagerMemoryAcceptance = [ordered]@{
        metric = 'privateWorkingSetBytes'
        thresholdBytes = 180MB
        observedPass = $completeRenderingRun -and $null -ne $privateWorkingSetSummary -and $privateWorkingSetSummary.below180MiB
        note = 'Task Manager commonly labels binary MiB as MB. This evaluates ~5-second observed samples, not unsampled transient peaks or all possible layouts.'
    }
    privateCommit = Get-MemorySummary 'privateBytes'
    totalWorkingSet = Get-MemorySummary 'workingSetBytes'
    privateWorkingSet = $privateWorkingSetSummary
    trafficWindows = $trafficWindows
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
    assertions = $assertions
    note = 'Sampling every ~5s; peaks are observed samples, not continuous maxima. MiB=1048576 bytes. Overlay expiry/queue drops are separately reported; no forced GC or working-set trimming.'
} | ConvertTo-Json -Depth 8
