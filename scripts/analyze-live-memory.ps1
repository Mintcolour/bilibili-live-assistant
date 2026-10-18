param(
    [Parameter(Mandatory)][string[]]$Path,
    [ValidateRange(64,4096)][int]$LimitMiB = 160,
    [ValidateRange(60,14400)][int]$RequiredSeconds = 3600,
    [ValidateRange(0,7200)][int]$WarmupSeconds = 300
)
$ErrorActionPreference = 'Stop'
# Read only numeric resource logs. Never emit message payloads or settings.
function Summarize-Window([object[]]$Rows) {
    if ($Rows.Count -lt 2) { return $null }
    $first = $Rows[0]; $last = $Rows[-1]
    $duration = $last.seconds - $first.seconds
    $meanSeconds = ($Rows.seconds | Measure-Object -Average).Average
    $meanBytes = ($Rows.privateWorkingSetBytes | Measure-Object -Average).Average
    $denominator = 0.0; $numerator = 0.0
    $meanCommit = ($Rows.privateBytes | Measure-Object -Average).Average
    $commitNumerator = 0.0
    foreach ($row in $Rows) {
        $delta = $row.seconds - $meanSeconds
        $denominator += $delta * $delta
        $numerator += $delta * ($row.privateWorkingSetBytes - $meanBytes)
        $commitNumerator += $delta * ($row.privateBytes - $meanCommit)
    }
    $cpu = @($Rows.cpuPercent | Sort-Object)
    [ordered]@{
        fromSeconds = $first.seconds; toSeconds = $last.seconds; samples = $Rows.Count
        privateWorkingSetStartMiB = [math]::Round($first.privateWorkingSetBytes / 1MB, 2)
        privateWorkingSetEndMiB = [math]::Round($last.privateWorkingSetBytes / 1MB, 2)
        privateWorkingSetPeakMiB = [math]::Round(($Rows.privateWorkingSetBytes | Measure-Object -Maximum).Maximum / 1MB, 2)
        privateWorkingSetMinimumMiB = [math]::Round(($Rows.privateWorkingSetBytes | Measure-Object -Minimum).Minimum / 1MB, 2)
        privateWorkingSetMeanMiB = [math]::Round($meanBytes / 1MB, 2)
        privateWorkingSetSlopeMiBPerMinute = if ($denominator -gt 0) { [math]::Round(60 * $numerator / $denominator / 1MB, 4) } else { $null }
        privateCommitStartMiB = [math]::Round($first.privateBytes / 1MB, 2)
        privateCommitEndMiB = [math]::Round($last.privateBytes / 1MB, 2)
        privateCommitSlopeMiBPerMinute = if ($denominator -gt 0) { [math]::Round(60 * $commitNumerator / $denominator / 1MB, 4) } else { $null }
        managedEndMiB = [math]::Round($last.managedBytes / 1MB, 2)
        gcCommittedEndMiB = if ($null -ne $last.gcCommittedBytes) { [math]::Round($last.gcCommittedBytes / 1MB, 2) } else { $null }
        finalizersPendingEnd = $last.gcFinalizationPending
        receivedEvents = $last.ui.received - $first.ui.received
        receivedPerSecond = if ($duration -gt 0) { [math]::Round(($last.ui.received - $first.ui.received) / $duration, 2) } else { $null }
        createdDanmakuCards = $last.ui.window.dashboard.danmaku.createdCards - $first.ui.window.dashboard.danmaku.createdCards
        reboundDanmakuCards = $last.ui.window.dashboard.danmaku.reboundCards - $first.ui.window.dashboard.danmaku.reboundCards
        pooledDanmakuCards = $last.ui.window.dashboard.danmaku.pooledCards - $first.ui.window.dashboard.danmaku.pooledCards
        movedDanmakuCards = if ($null -ne $last.ui.window.dashboard.danmaku.movedCards) {
            $last.ui.window.dashboard.danmaku.movedCards - $first.ui.window.dashboard.danmaku.movedCards
        } else { $null }
        decodedImages = $last.ui.imageResources.decodeCount - $first.ui.imageResources.decodeCount
        cachedPixelEndMiB = [math]::Round($last.ui.images.staticPixelBytes / 1MB, 2)
        pixelBudgetEndMiB = if ($null -ne $last.ui.images.memoryBudgetBytes) {
            [math]::Round($last.ui.images.memoryBudgetBytes / 1MB, 2)
        } else { $null }
        pressureEvictedImages = if ($null -ne $last.ui.images.pressureEvicted) {
            $last.ui.images.pressureEvicted - $first.ui.images.pressureEvicted
        } else { $null }
        visibleBindingCumulativeP95UpperMs = $last.ui.imageResources.bindingReadyLatency.all.p95UpperMs
        waitedVisibleBindingsOver2Seconds = if ($null -ne $last.ui.imageResources.bindingReadyLatency.waited.over2000ms) {
            $last.ui.imageResources.bindingReadyLatency.waited.over2000ms - $first.ui.imageResources.bindingReadyLatency.waited.over2000ms
        } else { $null }
        cpuP95 = $cpu[[math]::Ceiling($cpu.Count * .95) - 1]
    }
}
$results = foreach ($directory in $Path) {
    $root = (Resolve-Path -LiteralPath $directory).Path
    $records = @(Get-Content -LiteralPath (Join-Path $root 'samples.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    $header = $records | Where-Object kind -eq header | Select-Object -First 1
    if ($header.config.LiveRoom -le 0) { throw '此脚本只分析真实直播观察日志' }
    $samples = @($records | Where-Object kind -eq sample)
    $interferenceFile = Join-Path $root 'diagnostic-interference.json'
    $interference = Test-Path -LiteralPath $interferenceFile
    $diagnosticFromSeconds = $null
    if ($interference) {
        $marker = Get-Content -LiteralPath $interferenceFile -Raw | ConvertFrom-Json
        $diagnosticFromSeconds = ([DateTimeOffset]$marker.timestamp - [DateTimeOffset]$header.timestamp).TotalSeconds
        # A heap probe/dump can alter the target. Do not fold its later values
        # into normal resource windows or certify that run as a clean acceptance.
        $samples = @($samples | Where-Object { $_.seconds -lt $diagnosticFromSeconds })
    }
    $traffic = @($samples | Where-Object phase -eq traffic)
    if ($traffic.Count -lt 2) { throw '真实流量采样尚不足两条' }
    $last = $traffic[-1]; $duration = $last.seconds - $traffic[0].seconds
    $postWarmup = @($traffic | Where-Object { $_.seconds -ge $traffic[0].seconds + $WarmupSeconds })
    $visible = @($traffic | Where-Object {
        $_.ui.window.mainVisible -ne $true -or $_.ui.window.page -ne 'dashboard' -or $_.ui.window.overlay.compact.visible -ne $true
    }).Count -eq 0
    $gap = 0.0
    for ($i = 1; $i -lt $traffic.Count; $i++) { $gap = [math]::Max($gap, $traffic[$i].seconds - $traffic[$i-1].seconds) }
    $peak = ($samples.privateWorkingSetBytes | Measure-Object -Maximum).Maximum
    $missingMetric = @($samples | Where-Object { $null -eq $_.privateWorkingSetBytes }).Count -gt 0
    $monitoring = @($traffic | Where-Object { $_.ui.monitoring -ne $true }).Count -eq 0
    $connectedFraction = @($traffic | Where-Object { $_.ui.connected -eq $true }).Count / [double]$traffic.Count
    $coverage = !$interference -and $duration -ge $RequiredSeconds -and $gap -le 30 -and $visible -and !$missingMetric -and
        $monitoring -and $connectedFraction -ge .9 -and
        $traffic.Count -ge [math]::Floor($RequiredSeconds / 5 * .9) -and $last.ui.received -gt $traffic[0].ui.received
    $windows = @(for ($minute = 0; $minute * 60 -lt $last.seconds; $minute += 5) {
        $window = @($traffic | Where-Object { $_.seconds -ge $minute * 60 -and $_.seconds -lt ($minute + 5) * 60 })
        if ($window.Count -gt 1) { Summarize-Window $window }
    })
    [ordered]@{
        name = $header.config.Name; room = $header.config.LiveRoom; processId = $header.pid
        anonymous = if ($null -ne $header.anonymousReading) { $header.anonymousReading } else { $header.config.UseCurrentAccount -ne $true }
        isolatedHistory = $true; inheritedAppearance = ![string]::IsNullOrEmpty($header.config.AppearanceSettingsPath)
        observedSeconds = $duration; requiredSeconds = $RequiredSeconds; coverageComplete = $coverage
        diagnosticInterference = $interference; diagnosticFromSeconds = $diagnosticFromSeconds
        overviewAndCompactVisibleThroughout = $visible; maximumSampleGapSeconds = $gap
        monitoringThroughout = $monitoring; connectedSampleFraction = [math]::Round($connectedFraction, 4)
        limitMiB = $LimitMiB; privateWorkingSetPeakMiB = [math]::Round($peak / 1MB, 2)
        evaluationObjective = 'post-warmup-stability'
        limitIsReferenceOnly = $true
        warmupSeconds = $WarmupSeconds
        postWarmup = Summarize-Window $postWarmup
        stabilityPass = $null
        legacyThresholdPass = $coverage -and $peak -le $LimitMiB * 1MB
        # Retain the old field for consumers; it only tests the legacy limit.
        # Neither this field nor complete coverage certifies memory stability.
        observedMemoryPass = $coverage -and $peak -le $LimitMiB * 1MB
        endedNormally = Test-Path -LiteralPath (Join-Path $root 'completed.json')
        archiveDropped = $last.ui.history.dropped; archiveFaulted = $last.ui.history.faulted
        totalReceived = $last.ui.received; overviewCardsCreated = $last.ui.window.dashboard.danmaku.createdCards
        overviewCardsRebound = $last.ui.window.dashboard.danmaku.reboundCards
        windows = $windows
        note = '当前目标为预热后收敛，limitMiB 与 observedMemoryPass 仅保留旧阈值口径，不自动判定稳定或泄漏。结合分段斜率、谷值、提交量及流量评估。约五秒采样，非瞬时峰值保证；真实流量不等于固定负载，多进程会相互影响。隔离运行不替代正式应用及完整功能验收。'
    }
}
ConvertTo-Json -InputObject @($results) -Depth 12
