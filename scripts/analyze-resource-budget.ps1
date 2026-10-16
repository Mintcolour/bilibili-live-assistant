param(
    [Parameter(Mandatory)][string]$Path,
    [string[]]$BaselineRuns = @(),
    [ValidateRange(60,14400)][int]$RequiredTrafficSeconds = 3600
)
$ErrorActionPreference = 'Stop'
$runPath = (Resolve-Path -LiteralPath $Path).Path
$rows = @(Get-Content -LiteralPath (Join-Path $runPath 'samples.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
$header = $rows | Where-Object kind -eq header | Select-Object -First 1
$samples = @($rows | Where-Object kind -eq sample)
if (!$header -or !$samples.Count) { throw '没有完整表头或采样数据' }
$traffic = @($samples | Where-Object phase -eq traffic)
$last = $samples[-1]
function Percentile([object[]]$Values, [double]$Fraction) {
    $sorted = @($Values | Where-Object { $null -ne $_ } | Sort-Object { [double]$_ })
    if (!$sorted.Count) { return $null }
    return [double]$sorted[[math]::Max(0, [math]::Ceiling($sorted.Count * $Fraction) - 1)]
}
function StartupMedian([object[]]$Rows) {
    $values = @($Rows | Where-Object { $_.kind -eq 'sample' -and $_.phase -eq 'startup_baseline' -and $_.seconds -ge 30 -and $_.seconds -le 60 } |
        ForEach-Object { $_.privateWorkingSetBytes } | Where-Object { $null -ne $_ } | Sort-Object)
    if ($values.Count -lt 4) { return $null }
    if ($values.Count % 2) { return [double]$values[[int][math]::Floor($values.Count / 2)] }
    return ([double]$values[$values.Count / 2 - 1] + [double]$values[$values.Count / 2]) / 2
}
function Slope([object[]]$Window, [string]$Field) {
    if ($Window.Count -lt 3 -or @($Window | Where-Object { $null -eq $_.$Field }).Count) { return $null }
    $origin = [double]$Window[0].seconds
    $meanX = ($Window | ForEach-Object { $_.seconds - $origin } | Measure-Object -Average).Average
    $meanY = ($Window | ForEach-Object { $_.$Field } | Measure-Object -Average).Average
    $denominator = 0.0; $numerator = 0.0
    foreach ($row in $Window) {
        $x = $row.seconds - $origin - $meanX
        $denominator += $x * $x; $numerator += $x * ($row.$Field - $meanY)
    }
    if ($denominator -le 0) { return $null }
    return 60 * $numerator / $denominator / 1MB
}
function Memory([string]$Field) {
    $values = @($samples | ForEach-Object { $_.$Field } | Where-Object { $null -ne $_ })
    if (!$values.Count) { return $null }
    return [ordered]@{ peakBytes=($values | Measure-Object -Maximum).Maximum; lastBytes=$values[-1]; samples=$values.Count }
}
function BindingLatency([string]$Group) {
    if (!$traffic.Count) { return $null }
    $latest = $traffic[-1].ui.imageResources.bindingReadyLatency.$Group
    if ($null -eq $latest) { return $null }
    if ($null -ne $latest.PSObject.Properties['p95UpperMs']) { return $latest }
    $before = $samples | Where-Object seconds -lt $traffic[0].seconds | Select-Object -Last 1
    $previous = @{}
    foreach ($bin in $before.ui.imageResources.bindingReadyLatency.$Group) {
        $key = if ($null -eq $bin.upperMs) { 2001 } else { [int]$bin.upperMs }
        $previous[$key] = [long]$bin.count
    }
    $histogram = @(foreach ($bin in $latest) {
        $key = if ($null -eq $bin.upperMs) { 2001 } else { [int]$bin.upperMs }
        $count = [long]$bin.count - [long]$previous[$key]
        if ($count -gt 0) { [pscustomobject]@{ upperMs=$key; count=$count } }
    })
    $total = ($histogram | Measure-Object count -Sum).Sum
    if (!$total) { return [ordered]@{ count=0; p95UpperMs=$null; over2000ms=0 } }
    $cumulative = 0L; $p95 = $null
    foreach ($bin in $histogram | Sort-Object upperMs) {
        $cumulative += $bin.count
        if ($cumulative -ge [math]::Ceiling($total * 0.95)) { if ($bin.upperMs -le 2000) { $p95=$bin.upperMs }; break }
    }
    return [ordered]@{ count=$total; p95UpperMs=$p95; over2000ms=[long]($histogram | Where-Object upperMs -eq 2001 | Measure-Object count -Sum).Sum }
}
$startup = StartupMedian $rows
$baselineValues = [Collections.Generic.List[double]]::new()
$baselineKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($baselinePath in $BaselineRuns) {
    $resolved = (Resolve-Path -LiteralPath $baselinePath).Path
    if (!$baselineKeys.Add($resolved)) { throw '启动基准目录重复，不能算作三次独立启动' }
    $baselineRows = @(Get-Content -LiteralPath (Join-Path $resolved 'samples.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    $baselineHeader = $baselineRows | Where-Object kind -eq header | Select-Object -First 1
    if (!$baselineHeader.build.appSha256 -or $baselineHeader.build.appSha256 -ne $header.build.appSha256 -or
        $baselineHeader.winUiVersion -ne $header.winUiVersion -or $baselineHeader.dpi -ne $header.dpi -or
        $baselineHeader.config.View -ne $header.config.View -or $baselineHeader.build.appSdkVersion -ne $header.build.appSdkVersion -or
        $baselineHeader.config.EntryAvatars -ne $header.config.EntryAvatars -or
        $baselineHeader.config.AnimateEmotes -ne $header.config.AnimateEmotes) {
        throw '启动基准的程序哈希、DPI、SDK 或窗口配置不一致'
    }
    $value = StartupMedian $baselineRows
    if ($null -eq $value) { throw '启动基准缺少 30–60 秒采样' }
    $baselineValues.Add($value)
}
$threeBaselines = $baselineValues.Count -eq 3
$m0 = if ($threeBaselines) { Percentile ($baselineValues.ToArray()) 0.5 } else { $startup }
$budget = if ($null -ne $m0) { [math]::Min(160MB, 2 * $m0) } else { 160MB }
$completed = Test-Path -LiteralPath (Join-Path $runPath 'completed.json')
$assertions = $rows | Where-Object kind -eq assertions | Select-Object -Last 1
$retention = $null -ne $assertions -and $assertions.stopRetainsContent -and $assertions.sameRoomRetainsContent -and
    $assertions.offlineRetainsContent -and $assertions.switchClearsContent
$visible = $traffic.Count -gt 0 -and @($traffic | Where-Object {
    $_.ui.window.mainVisible -ne $true -or $_.ui.window.page -ne 'dashboard' -or $_.ui.window.overlay.visible -ne $true
}).Count -eq 0
$maxGap = 0.0
for ($i = 1; $i -lt $traffic.Count; $i++) { $maxGap = [math]::Max($maxGap, $traffic[$i].seconds - $traffic[$i - 1].seconds) }
$trafficSeconds = if ($traffic.Count -gt 1) { $traffic[-1].seconds - $traffic[0].seconds } else { 0 }
$fullTraffic = $trafficSeconds -ge ($RequiredTrafficSeconds - 10) -and $header.config.Seconds -ge $RequiredTrafficSeconds -and
    $traffic.Count -ge [math]::Floor($RequiredTrafficSeconds / 5 * 0.9) -and $maxGap -le 30
$meterPath = Join-Path $runPath 'process-250ms.jsonl'
$meter = $null
if (Test-Path -LiteralPath $meterPath) {
    $reader = [IO.StreamReader]::new([IO.FileStream]::new($meterPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite))
    try {
        $meterHeader = $null; $meterCount = 0; $meterPeak = 0L; $over = 0; $over160 = 0; $first = $null; $previous = $null; $meterGap = 0.0
        while ($null -ne ($line = $reader.ReadLine())) {
            try { $row = $line | ConvertFrom-Json } catch { continue }
            if ($row.kind -eq 'header') { $meterHeader = $row; continue }
            if ($row.kind -ne 'sample' -or $null -eq $row.privateWorkingSetBytes) { continue }
            $meterCount++; $meterPeak = [math]::Max($meterPeak, [long]$row.privateWorkingSetBytes)
            if ($row.privateWorkingSetBytes -gt $budget) { $over++ }
            if ($row.privateWorkingSetBytes -gt 160MB) { $over160++ }
            if ($null -eq $first) { $first = [double]$row.seconds }
            if ($null -ne $previous) { $meterGap = [math]::Max($meterGap, $row.seconds - $previous) }
            $previous = [double]$row.seconds
        }
        $offset = if ($meterHeader) { ([DateTimeOffset]$meterHeader.utc - [DateTimeOffset]$header.timestamp).TotalSeconds } else { $null }
        $matchingPid = $meterHeader -and $meterHeader.pid -eq $header.pid
        $covered = $matchingPid -and $null -ne $offset -and $null -ne $first -and $traffic.Count -gt 1 -and
            $first + $offset -le $traffic[0].seconds + 1 -and $previous + $offset -ge $traffic[-1].seconds - 1 -and $meterGap -le 1
        $meter = [ordered]@{ samples=$meterCount; peakBytes=$meterPeak; overBudgetSamples=$over; over160MiBSamples=$over160;
            maximumGapSeconds=$meterGap; matchingPid=[bool]$matchingPid; coversTraffic=[bool]$covered; intervalMs=$meterHeader.intervalMs;
            startsAtAppSeconds=if ($null -ne $first -and $null -ne $offset) { $first + $offset } else { $null } }
    } finally { $reader.Dispose() }
}
$privateWs = Memory 'privateWorkingSetBytes'
$observedFailure = ($privateWs -and $privateWs.peakBytes -gt $budget) -or ($meter -and $meter.overBudgetSamples -gt 0)
$eligible = $header.config.View -eq 'both' -and $header.config.Images -eq 'unique' -and !$header.config.Snapshot -and !$header.config.ScrollProbe -and !$header.config.LifecycleProbe -and
    !$header.config.DrawingDecode -and $header.config.AttachImages -ne $false -and $header.config.CreateSources -ne $false -and !$header.build.alternateSource
$complete = $completed -and $fullTraffic -and $visible -and $retention -and $threeBaselines -and $meter -and $meter.coversTraffic -and $eligible
$tail = @($traffic | Where-Object { $_.seconds -ge ($traffic[-1].seconds - 1800) })
$allocationRate = if ($traffic.Count -gt 1) { ($traffic[-1].allocatedBytes - $traffic[0].allocatedBytes) / $trafficSeconds / 1MB } else { $null }
[ordered]@{
    schema=1; name=$header.config.Name; configuration=$header.config; build=$header.build; dpi=$header.dpi; winUiVersion=$header.winUiVersion;
    startup=[ordered]@{ runMedianBytes=$startup; baselineMediansBytes=$baselineValues.ToArray(); threeIndependentBaselines=$threeBaselines; m0Bytes=$m0;
        budgetBytes=$budget; provisional=!$threeBaselines; note='M0 is the median at 30–60 seconds; three matching independent startups are required. A single run is provisional.' };
    acceptance=[ordered]@{ state=if ($observedFailure) { 'failed-observed-limit' } elseif ($complete) { 'memory-observed-pass' } else { 'incomplete' };
        completed=$completed; eligible=$eligible; fullTraffic=$fullTraffic; visibleThroughout=$visible; retentionPassed=[bool]$retention;
        trafficSeconds=$trafficSeconds; maximumUiSampleGapSeconds=$maxGap; requiredTrafficSeconds=$RequiredTrafficSeconds;
        note='A memory result does not prove CPU, image latency, DPI compatibility or real-user acceptance. Finite sampling cannot exclude inter-sample peaks.' };
    externalMeter=$meter; privateWorkingSet=$privateWs; privateCommit=(Memory 'privateBytes'); totalWorkingSet=(Memory 'workingSetBytes');
    cpu=[ordered]@{ p50Percent=(Percentile $traffic.cpuPercent 0.5); p95Percent=(Percentile $traffic.cpuPercent 0.95); normalizedByProcessors=$header.processors;
        minimumTargetFps=($traffic.ui.window.overlay.clock.targetFps | Measure-Object -Minimum).Minimum;
        maximumTargetFps=($traffic.ui.window.overlay.clock.targetFps | Measure-Object -Maximum).Maximum };
    allocationMiBPerSecond=$allocationRate;
    winUiBindingReadyLatency=[ordered]@{ all=(BindingLatency 'all'); waited=(BindingLatency 'waited');
        note='Successful visible-row bindings, cumulative at last traffic sample. 1 ms buckets, >2000 ms overflow. Not GPU presentation, overlay appearance, failures or cancelled waits.' };
    lastHalfHourSlopeMiBPerMinute=[ordered]@{ privateWorkingSet=(Slope $tail 'privateWorkingSetBytes'); privateCommit=(Slope $tail 'privateBytes'); managed=(Slope $tail 'managedBytes') };
    received=$last.ui.received; archived=$last.ui.history.writtenEvents; archiveDropped=$last.ui.history.dropped; archiveWriteFailures=$last.ui.history.writeFailures;
    images=$last.ui.images; resources=$last.ui.imageResources; uiImages=$last.ui.uiImages; assertions=$assertions;
    note='Use matching display/remote-desktop conditions for CPU comparisons. Loopback fixtures exercise a finite set of image bodies; URL rotation is not unique image content.'
} | ConvertTo-Json -Depth 12
