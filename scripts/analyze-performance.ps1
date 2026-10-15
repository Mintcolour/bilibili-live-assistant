param(
    [string]$LogDirectory = (Join-Path $env:LOCALAPPDATA 'BilibiliLiveAssistant/diagnostics'),
    [string]$Session = '',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
# Read only the four bounded numeric diagnostic logs. Never open settings or history.db.
$samples = [System.Collections.Generic.List[object]]::new()
$skipped = 0
foreach ($index in 0..3) {
    $path = Join-Path $LogDirectory "performance.$index.jsonl"
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $stream = [System.IO.FileStream]::new($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $reader = [System.IO.StreamReader]::new($stream)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            try { $row = ConvertFrom-Json -InputObject $line -ErrorAction Stop }
            catch { $skipped++; continue } # A crash or an ongoing append may leave a partial final line.
            if ($row.kind -eq 'sample') { $samples.Add($row) }
        }
    } finally { $reader.Dispose() }
}
if ($samples.Count -eq 0) { throw '没有可分析的性能采样，请先在软件的“性能诊断”页记录至少 5 秒。' }
if (-not $Session) { $Session = ($samples | Sort-Object utc | Select-Object -Last 1).session }
$ordered = @($samples | Where-Object session -EQ $Session | Sort-Object uptimeSeconds)
if ($ordered.Count -eq 0) { throw '未找到指定采样会话。' }

function Percentile([object[]]$Values, [double]$Fraction) {
    $valid = @($Values | Where-Object { $null -ne $_ } | Sort-Object { [double]$_ })
    if ($valid.Count -eq 0) { return $null }
    return [math]::Round([double]$valid[[math]::Max(0, [math]::Ceiling($valid.Count * $Fraction) - 1)], 3)
}
function Growth([object[]]$Rows, [string]$Field) {
    # Least-squares MiB/min; cache warm-up and native allocations can cause growth too.
    if ($Rows.Count -lt 3) { return $null }
    $origin = [double]$Rows[0].uptimeSeconds
    $xs = @($Rows | ForEach-Object { ([double]$_.uptimeSeconds - $origin) / 60 })
    $ys = @($Rows | ForEach-Object { [double]$_.$Field / 1MB })
    $mx = ($xs | Measure-Object -Average).Average
    $my = ($ys | Measure-Object -Average).Average
    $num = 0.0; $den = 0.0
    for ($i = 0; $i -lt $Rows.Count; $i++) { $dx = $xs[$i] - $mx; $num += $dx * ($ys[$i] - $my); $den += $dx * $dx }
    if ($den -eq 0) { return $null }
    return [math]::Round($num / $den, 3)
}
function Get-Delta([object[]]$Rows, [scriptblock]$Selector) {
    if ($Rows.Count -lt 2) { return $null }
    $last = & $Selector $Rows[-1]; $first = & $Selector $Rows[0]
    if ($null -eq $last -or $null -eq $first) { return $null }
    return $last - $first
}
$labels = @('空闲 / 无弹幕', '正常监视', '大量弹幕', '主窗或浮窗隐藏', '浏览历史', '调整浮窗 / 设置预览')
# Preserve contiguous phases; don't merge warm-up and later idle into a misleading trend.
$segments = [System.Collections.Generic.List[object]]::new()
$part = [System.Collections.Generic.List[object]]::new()
foreach ($row in $ordered) {
    if ($part.Count -gt 0 -and $part[0].scenario -ne $row.scenario) { $segments.Add($part.ToArray()); $part.Clear() }
    $part.Add($row)
}
if ($part.Count -gt 0) { $segments.Add($part.ToArray()) }
$phases = foreach ($segment in $segments) {
    $rows = @($segment)
    $seconds = [double]$rows[-1].uptimeSeconds - [double]$rows[0].uptimeSeconds
    $allSeconds = ($rows | Measure-Object intervalSeconds -Sum).Sum
    $operations = @($rows | Where-Object uiFresh | ForEach-Object { $_.timings.operations })
    $timings = foreach ($group in ($operations | Group-Object name)) {
        $count = ($group.Group | Measure-Object count -Sum).Sum
        $total = ($group.Group | Measure-Object totalMs -Sum).Sum
        [pscustomobject]@{ Name = $group.Name; Count = $count; TotalMs = [math]::Round($total, 2)
            MeanMs = $(if ($count -gt 0) { [math]::Round($total / $count, 3) } else { 0 })
            MaxMs = ($group.Group | Measure-Object maxMs -Maximum).Maximum }
    }
    $received = Get-Delta $rows { param($r) $r.ui.received }
    [pscustomobject]@{
        Scenario = $labels[[int]$rows[0].scenario]; StartUtc = $rows[0].utc; EndUtc = $rows[-1].utc
        Samples = $rows.Count; SpanSeconds = [math]::Round($seconds, 1)
        CpuP50 = Percentile @($rows.cpuPercent) 0.5; CpuP95 = Percentile @($rows.cpuPercent) 0.95; CpuMax = Percentile @($rows.cpuPercent) 1
        PrivatePeakMiB = [math]::Round(($rows | Measure-Object privateBytes -Maximum).Maximum / 1MB, 2)
        WorkingSetPeakMiB = [math]::Round(($rows | Measure-Object workingSetBytes -Maximum).Maximum / 1MB, 2)
        ManagedPeakMiB = [math]::Round(($rows | Measure-Object managedBytes -Maximum).Maximum / 1MB, 2)
        PrivateMiBPerMinute = Growth $rows 'privateBytes'; ManagedMiBPerMinute = Growth $rows 'managedBytes'
        AllocatedMiBPerSecond = $(if ($allSeconds -gt 0) { [math]::Round(($rows | Measure-Object allocatedBytes -Sum).Sum / 1MB / $allSeconds, 3) } else { $null })
        EventsPerSecond = $(if ($seconds -gt 0) { [math]::Round($received / $seconds, 2) } else { $null })
        UiDelayP95Ms = Percentile @($rows | Where-Object uiFresh | ForEach-Object uiDelayMs) 0.95
        UiAgeMaxMs = Percentile @($rows.uiAgeMs) 1
        UiPendingPeak = ($rows.ui | Measure-Object pending -Maximum).Maximum
        ImageCachePeakMiB = [math]::Round(($rows.ui.images | Measure-Object estimatedBytes -Maximum).Maximum / 1MB, 2)
        CanvasPeakMiB = [math]::Round(($rows.ui | Measure-Object canvasBytes -Maximum).Maximum / 1MB, 2)
        ArchivePendingPeak = ($rows.ui.history | Measure-Object pending -Maximum).Maximum
        BufferDroppedDelta = Get-Delta $rows { param($r) $r.ui.bufferDropped }
        OverlayDroppedDelta = Get-Delta $rows { param($r) $r.ui.overlayDropped }
        ArchiveDroppedDelta = Get-Delta $rows { param($r) $r.ui.history.dropped }
        Gen2Delta = Get-Delta $rows { param($r) $r.gen2 }
        GdiObjectsPeak = ($rows | Measure-Object gdiObjects -Maximum).Maximum
        FrameGapMaxMs = ($rows.timings.frameCadence | Measure-Object maxGapMs -Maximum).Maximum
        LateFrames = ($rows.timings.frameCadence | Measure-Object lateFrames -Sum).Sum
        Timings = @($timings)
    }
}
$report = [pscustomobject]@{ Session = $Session; Samples = $ordered.Count; SkippedPartialLines = $skipped
    Notes = @('CPU 为本进程按可用逻辑处理器数归一化的百分比；不含 BrowserHost/浏览器。',
        '内存增长率至少 3 个点，仅作定位线索；缓存预热、GC 和原生内存都会影响，不能直接认定泄漏。',
        'timings 为 UI 脉冲间的区间统计；嵌套测量会重叠，不要相加当作 CPU。frameGap 是回调间隔，不是屏幕实际呈现帧率。',
        '切换场景前后约一个采样区间可能含过渡活动。原始日志滚动覆盖后只分析保留下来的区间。')
    Phases = @($phases) }
$json = $report | ConvertTo-Json -Depth 12
if ($OutputPath) { [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputPath), $json, [System.Text.UTF8Encoding]::new($false)) }
$json
