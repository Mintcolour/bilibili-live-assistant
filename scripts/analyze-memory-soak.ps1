param(
    [Parameter(Mandatory)][string]$Path,
    [ValidateRange(0,14400)][int]$ObservationSeconds = 0
)
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
$frameObservationSeconds = 0.0
for ($i = 1; $i -lt $samples.Count; $i++) {
    if ($samples[$i].phase -eq 'traffic') { $frameObservationSeconds += $samples[$i].seconds - $samples[$i - 1].seconds }
}
$targetRates = @($traffic | ForEach-Object { $_.ui.window.overlay.clock.targetFps } | Where-Object { $null -ne $_ })
$maxTrafficGap = 0.0
for ($i = 1; $i -lt $traffic.Count; $i++) { $maxTrafficGap = [math]::Max($maxTrafficGap, $traffic[$i].seconds - $traffic[$i - 1].seconds) }
$visibleThroughout = $traffic.Count -gt 0 -and @($traffic | Where-Object {
    $_.ui.window.mainVisible -ne $true -or $_.ui.window.page -ne 'dashboard' -or $_.ui.window.overlay.visible -ne $true
}).Count -eq 0
$samplingComplete = $traffic.Count -ge [math]::Floor($header.config.Seconds / 5 * 0.9) -and $maxTrafficGap -le 30 -and
    $traffic.Count -gt 1 -and ($traffic[-1].seconds - $traffic[0].seconds) -ge ($header.config.Seconds - 10)
$completeRenderingRun = $eligible -and $buildVerified -and $retentionPassed -and $null -ne $completed -and
    $visibleThroughout -and $samplingComplete -and $header.config.Seconds -ge 7200 -and
    $completed.seconds -ge 7200 -and $completed.produced -ge 7200 * $header.config.Rate
$privateWorkingSetSummary = Get-MemorySummary 'privateWorkingSetBytes'
$privateWorkingSetMeasured = @($samples | Where-Object { $null -eq $_.privateWorkingSetBytes }).Count -eq 0
$observationEndPath = Join-Path $runPath 'observation-ended.json'
$observationEnd = if (Test-Path -LiteralPath $observationEndPath) { Get-Content -LiteralPath $observationEndPath -Raw | ConvertFrom-Json } else { $null }
$observedTrafficSeconds = if ($traffic.Count -gt 1) { $traffic[-1].seconds - $traffic[0].seconds } else { 0 }
$observationComplete = $ObservationSeconds -ge 60 -and $eligible -and $buildVerified -and $visibleThroughout -and
    ($null -ne $completed -or ($null -ne $observationEnd -and $observationEnd.acceptedTrafficSeconds -eq $ObservationSeconds)) -and
    $observedTrafficSeconds -ge $ObservationSeconds -and $maxTrafficGap -le 30 -and
    $traffic.Count -ge [math]::Floor($ObservationSeconds / 5 * 0.9) -and $last.produced -ge $ObservationSeconds * $header.config.Rate
$memoryScopeComplete = if ($ObservationSeconds -gt 0) { $observationComplete } else { $completeRenderingRun }
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
    requestedObservation = [ordered]@{
        seconds = $ObservationSeconds
        observedTrafficSecondsLowerBound = $observedTrafficSeconds
        completed = $observationComplete
        userEnded = $null -ne $observationEnd
        normalHarnessCompletion = $null -ne $completed
        note = 'An explicitly requested observation can end without the harness final retention assertions or graceful archive drain. These are reported separately and are not synthesized.'
    }
    displayAndSampling = [ordered]@{ visibleThroughout = $visibleThroughout; samplingComplete = $samplingComplete; trafficSamples = $traffic.Count; maximumGapSeconds = $maxTrafficGap }
    taskManagerMemoryAcceptance = [ordered]@{
        metric = 'privateWorkingSetBytes'
        thresholdBytes = 180MB
        scope = if ($ObservationSeconds -gt 0) { 'explicitly-requested-observation' } else { 'complete-two-hour-run' }
        observedPass = $memoryScopeComplete -and $privateWorkingSetMeasured -and $null -ne $privateWorkingSetSummary -and $privateWorkingSetSummary.below180MiB
        note = 'Task Manager commonly labels binary MiB as MB. This evaluates ~5-second observed samples, not unsampled transient peaks or all possible layouts.'
    }
    privateCommit = Get-MemorySummary 'privateBytes'
    totalWorkingSet = Get-MemorySummary 'workingSetBytes'
    privateWorkingSet = $privateWorkingSetSummary
    trafficWindows = $trafficWindows
    cpuP95Percent = if ($cpu.Count) { $cpu[[math]::Min($cpu.Count-1, [math]::Ceiling($cpu.Count * 0.95)-1)] } else { $null }
    allocationMiBPerSecond = $allocationRate
    frameIntervals = $frameIntervals
    frameRateCondition = [ordered]@{
        minimumTargetFps = ($targetRates | Measure-Object -Minimum).Minimum
        maximumTargetFps = ($targetRates | Measure-Object -Maximum).Maximum
        observedCallbacksPerSecond = if ($frameObservationSeconds -gt 0) { [math]::Round($frameIntervals / $frameObservationSeconds, 2) } else { $null }
        note = 'Callbacks are measured over traffic sample intervals and are not GPU-presented FPS. Display/remote-desktop refresh limits must accompany performance comparisons.'
    }
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
