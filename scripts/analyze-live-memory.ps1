param(
    [Parameter(Mandatory)][string[]]$Path,
    [ValidateRange(64,4096)][int]$LimitMiB = 160,
    [ValidateRange(60,14400)][int]$RequiredSeconds = 3600
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
    foreach ($row in $Rows) {
        $delta = $row.seconds - $meanSeconds
        $denominator += $delta * $delta
        $numerator += $delta * ($row.privateWorkingSetBytes - $meanBytes)
    }
    $cpu = @($Rows.cpuPercent | Sort-Object)
    [ordered]@{
        fromSeconds = $first.seconds; toSeconds = $last.seconds; samples = $Rows.Count
        privateWorkingSetStartMiB = [math]::Round($first.privateWorkingSetBytes / 1MB, 2)
        privateWorkingSetEndMiB = [math]::Round($last.privateWorkingSetBytes / 1MB, 2)
        privateWorkingSetPeakMiB = [math]::Round(($Rows.privateWorkingSetBytes | Measure-Object -Maximum).Maximum / 1MB, 2)
        privateWorkingSetSlopeMiBPerMinute = if ($denominator -gt 0) { [math]::Round(60 * $numerator / $denominator / 1MB, 4) } else { $null }
        privateCommitEndMiB = [math]::Round($last.privateBytes / 1MB, 2)
        managedEndMiB = [math]::Round($last.managedBytes / 1MB, 2)
        receivedEvents = $last.ui.received - $first.ui.received
        receivedPerSecond = if ($duration -gt 0) { [math]::Round(($last.ui.received - $first.ui.received) / $duration, 2) } else { $null }
        createdDanmakuCards = $last.ui.window.dashboard.danmaku.createdCards - $first.ui.window.dashboard.danmaku.createdCards
        reboundDanmakuCards = $last.ui.window.dashboard.danmaku.reboundCards - $first.ui.window.dashboard.danmaku.reboundCards
        decodedImages = $last.ui.imageResources.decodeCount - $first.ui.imageResources.decodeCount
        cpuP95 = $cpu[[math]::Ceiling($cpu.Count * .95) - 1]
    }
}
$results = foreach ($directory in $Path) {
    $root = (Resolve-Path -LiteralPath $directory).Path
    $records = @(Get-Content -LiteralPath (Join-Path $root 'samples.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    $header = $records | Where-Object kind -eq header | Select-Object -First 1
    if ($header.config.LiveRoom -le 0) { throw '此脚本只分析真实直播观察日志' }
    $samples = @($records | Where-Object kind -eq sample)
    $traffic = @($samples | Where-Object phase -eq traffic)
    if ($traffic.Count -lt 2) { throw '真实流量采样尚不足两条' }
    $last = $traffic[-1]; $duration = $last.seconds - $traffic[0].seconds
    $visible = @($traffic | Where-Object {
        $_.ui.window.mainVisible -ne $true -or $_.ui.window.page -ne 'dashboard' -or $_.ui.window.overlay.compact.visible -ne $true
    }).Count -eq 0
    $gap = 0.0
    for ($i = 1; $i -lt $traffic.Count; $i++) { $gap = [math]::Max($gap, $traffic[$i].seconds - $traffic[$i-1].seconds) }
    $peak = ($samples.privateWorkingSetBytes | Measure-Object -Maximum).Maximum
    $missingMetric = @($samples | Where-Object { $null -eq $_.privateWorkingSetBytes }).Count -gt 0
    $coverage = $duration -ge $RequiredSeconds -and $gap -le 30 -and $visible -and !$missingMetric -and
        $traffic.Count -ge [math]::Floor($RequiredSeconds / 5 * .9) -and $last.ui.received -gt $traffic[0].ui.received
    $windows = @(for ($minute = 0; $minute * 60 -lt $last.seconds; $minute += 5) {
        $window = @($traffic | Where-Object { $_.seconds -ge $minute * 60 -and $_.seconds -lt ($minute + 5) * 60 })
        if ($window.Count -gt 1) { Summarize-Window $window }
    })
    [ordered]@{
        name = $header.config.Name; room = $header.config.LiveRoom; processId = $header.pid
        anonymous = $true; isolatedHistory = $true; inheritedAppearance = ![string]::IsNullOrEmpty($header.config.AppearanceSettingsPath)
        observedSeconds = $duration; requiredSeconds = $RequiredSeconds; coverageComplete = $coverage
        overviewAndCompactVisibleThroughout = $visible; maximumSampleGapSeconds = $gap
        limitMiB = $LimitMiB; privateWorkingSetPeakMiB = [math]::Round($peak / 1MB, 2)
        observedMemoryPass = $coverage -and $peak -le $LimitMiB * 1MB
        endedNormally = Test-Path -LiteralPath (Join-Path $root 'completed.json')
        archiveDropped = $last.ui.history.dropped; archiveFaulted = $last.ui.history.faulted
        totalReceived = $last.ui.received; overviewCardsCreated = $last.ui.window.dashboard.danmaku.createdCards
        overviewCardsRebound = $last.ui.window.dashboard.danmaku.reboundCards
        windows = $windows
        note = '约五秒数值采样；非瞬时峰值保证。真实流量不等于固定负载；多进程同时观察会相互影响。匿名隔离运行不替代已登录正式应用、实际显示及完整功能验收。'
    }
}
ConvertTo-Json -InputObject @($results) -Depth 12
