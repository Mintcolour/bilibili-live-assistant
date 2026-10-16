param(
    [string]$LogDirectory = (Join-Path $env:LOCALAPPDATA 'BilibiliLiveAssistant/diagnostics'),
    [string]$Session = '',
    [Nullable[DateTimeOffset]]$FromTime = $null,
    [Nullable[DateTimeOffset]]$ToTime = $null,
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
if ($null -ne $FromTime -or $null -ne $ToTime) {
    $filtered = @($samples | Where-Object { ($null -eq $FromTime -or [DateTimeOffset]$_.utc -ge $FromTime) -and
        ($null -eq $ToTime -or [DateTimeOffset]$_.utc -le $ToTime) })
    $samples.Clear(); $samples.AddRange([object[]]$filtered)
}
if ($samples.Count -eq 0) { throw '指定时段没有可分析的性能采样。' }
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
function Optional-Peak([object[]]$Values) {
    $valid = @($Values | Where-Object { $null -ne $_ })
    if ($valid.Count -eq 0) { return $null }
    return ($valid | Measure-Object -Maximum).Maximum
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
        PrivateWorkingSetSamples = @($rows | Where-Object { $null -ne $_.privateWorkingSetBytes }).Count
        PrivateWorkingSetPeakMiB = $(
            $peak = Optional-Peak @($rows | ForEach-Object { $_.privateWorkingSetBytes })
            if ($null -ne $peak) { [math]::Round($peak / 1MB, 2) } else { $null })
        PrivateWorkingSetMiBPerMinute = $(
            $measured = @($rows | Where-Object { $null -ne $_.privateWorkingSetBytes })
            Growth $measured 'privateWorkingSetBytes')
        WorkingSetPeakMiB = [math]::Round(($rows | Measure-Object workingSetBytes -Maximum).Maximum / 1MB, 2)
        ManagedPeakMiB = [math]::Round(($rows | Measure-Object managedBytes -Maximum).Maximum / 1MB, 2)
        GcHeapPeakBytes = Optional-Peak @($rows | ForEach-Object { $_.gcHeapBytes })
        GcFragmentedPeakBytes = Optional-Peak @($rows | ForEach-Object { $_.gcFragmentedBytes })
        GcCommittedPeakBytes = Optional-Peak @($rows | ForEach-Object { $_.gcCommittedBytes })
        PrivateMiBPerMinute = Growth $rows 'privateBytes'; ManagedMiBPerMinute = Growth $rows 'managedBytes'
        AllocatedMiBPerSecond = $(if ($allSeconds -gt 0) { [math]::Round(($rows | Measure-Object allocatedBytes -Sum).Sum / 1MB / $allSeconds, 3) } else { $null })
        EventsPerSecond = $(if ($seconds -gt 0) { [math]::Round($received / $seconds, 2) } else { $null })
        UiDelayP95Ms = Percentile @($rows | Where-Object uiFresh | ForEach-Object uiDelayMs) 0.95
        UiAgeMaxMs = Percentile @($rows.uiAgeMs) 1
        UiPendingPeak = ($rows.ui | Measure-Object pending -Maximum).Maximum
        ImageCachePeakMiB = [math]::Round(($rows.ui.images | Measure-Object estimatedBytes -Maximum).Maximum / 1MB, 2)
        ImageLoadsDelta = Get-Delta $rows { param($r) $r.ui.images.loads }
        ImageEvictedDelta = Get-Delta $rows { param($r) $r.ui.images.evicted }
        ImageDiskHitsDelta = Get-Delta $rows { param($r) $r.ui.images.diskHits }
        ImageNetworkRequestsDelta = Get-Delta $rows { param($r) $r.ui.images.networkRequests }
        ImageNetworkBytesDelta = Get-Delta $rows { param($r) $r.ui.images.networkBytes }
        ImageDiskBytesDelta = Get-Delta $rows { param($r) $r.ui.images.diskBytes }
        ImageRawBodyBytesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.images.rawBodyBytesPeak })
        ImageExactLengthReadsDelta = Get-Delta $rows { param($r) $r.ui.images.exactLengthReads }
        ImageUnknownLengthReadsDelta = Get-Delta $rows { param($r) $r.ui.images.unknownLengthReads }
        ImageFrameHitsDelta = Get-Delta $rows { param($r) $r.ui.images.frameHits }
        ImageFrameMissesDelta = Get-Delta $rows { param($r) $r.ui.images.frameMisses }
        StaticPixelBytesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.images.staticPixelBytes })
        AnimationEncodedBytesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.images.animationEncodedBytes })
        AnimationFirstPixelBytesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.images.animationPixelBytes })
        ImageViewsPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.imageResources.views })
        ActiveImageViewsPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.imageResources.activeViews })
        DecodeInputBytesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.imageResources.decodeInputPeakBytes })
        DecodeStartedDelta = Get-Delta $rows { param($r) $r.ui.imageResources.decodesStarted }
        DecodeEndedDelta = Get-Delta $rows { param($r) $r.ui.imageResources.decodesEnded }
        ImageBindingAttemptsDelta = Get-Delta $rows { param($r) $r.ui.imageResources.bindingAttempts }
        ImageBindingChangesDelta = Get-Delta $rows { param($r) $r.ui.imageResources.bindingChanges }
        ImageRebindAttemptsDelta = Get-Delta $rows { param($r) $r.ui.imageResources.rebindAttempts }
        ImageRebindChangesDelta = Get-Delta $rows { param($r) $r.ui.imageResources.rebindChanges }
        NativeImageSourcesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.imageResources.nativeSources })
        NativeImagePixelBytesEstimatePeak = Optional-Peak @($rows | ForEach-Object { $_.ui.imageResources.nativePixelBytesEstimate })
        ImageBackingBitmapBytesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.imageResources.backingBitmapBytes })
        ImageDisplaySurfaceBytesEstimatePeak = Optional-Peak @($rows | ForEach-Object { $_.ui.imageResources.displaySurfaceBytesEstimate })
        ImageUploadQueuePeak = Optional-Peak @($rows | ForEach-Object { $_.ui.imageResources.pendingUploads })
        ActiveImageUploadsPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.imageResources.activeUploads })
        ImageStaleUploadsDelta = Get-Delta $rows { param($r) $r.ui.imageResources.staleUploads }
        UiImageLeasesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.uiImages.leases })
        UiImageSourcesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.uiImages.sources })
        UiImageIdleBytesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.uiImages.idleBytes })
        UiImageActiveBytesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.uiImages.activeBytes })
        UiImageReservedBytesPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.uiImages.reservedBytes })
        UiImagePendingPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.uiImages.pending })
        UiImageQueuedPeak = Optional-Peak @($rows | ForEach-Object { $_.ui.uiImages.queued })
        UiImageCreatedDelta = Get-Delta $rows { param($r) $r.ui.uiImages.created }
        UiImageReleasedDelta = Get-Delta $rows { param($r) $r.ui.uiImages.released }
        UiImageCancelledDelta = Get-Delta $rows { param($r) $r.ui.uiImages.cancelled }
        UiImageAbortedDelta = Get-Delta $rows { param($r) $r.ui.uiImages.aborted }
        UiImageStaleDelta = Get-Delta $rows { param($r) $r.ui.uiImages.stale }
        ImageDecodeDelta = Get-Delta $rows { param($r) $r.ui.imageResources.decodeCount }
        ImageConversionDelta = Get-Delta $rows { param($r) $r.ui.imageResources.conversionCount }
        ImageUploadDelta = Get-Delta $rows { param($r) $r.ui.imageResources.uploadCount }
        ImageDecodeMsDelta = Get-Delta $rows { param($r) $r.ui.imageResources.decodeMs }
        ImageConversionMsDelta = Get-Delta $rows { param($r) $r.ui.imageResources.conversionMs }
        ImageUploadMsDelta = Get-Delta $rows { param($r) $r.ui.imageResources.uploadMs }
        ImageReleaseMsDelta = Get-Delta $rows { param($r) $r.ui.imageResources.releaseMs }
        MainVisibleSamples = @($rows | Where-Object { $_.ui.window.mainVisible -eq $true }).Count
        MainHiddenSamples = @($rows | Where-Object { $_.ui.window.mainVisible -eq $false }).Count
        CanvasPeakMiB = [math]::Round(($rows.ui | Measure-Object canvasBytes -Maximum).Maximum / 1MB, 2)
        ArchivePendingPeak = ($rows.ui.history | Measure-Object pending -Maximum).Maximum
        BufferDroppedDelta = Get-Delta $rows { param($r) $r.ui.bufferDropped }
        OverlayDroppedDelta = Get-Delta $rows { param($r) $r.ui.overlayDropped }
        ArchiveDroppedDelta = Get-Delta $rows { param($r) $r.ui.history.dropped }
        Gen2Delta = Get-Delta $rows { param($r) $r.gen2 }
        GdiObjectsPeak = ($rows | Measure-Object gdiObjects -Maximum).Maximum
        FrameGapMaxMs = ($rows.timings.frameCadence | Measure-Object maxGapMs -Maximum).Maximum
        LateFrames = ($rows.timings.frameCadence | Measure-Object lateFrames -Sum).Sum
        BlitSourcePixels = ($rows.timings.blits | Measure-Object sourcePixels -Sum).Sum
        BlitSubmittedPixels = ($rows.timings.blits | Measure-Object submittedPixels -Sum).Sum
        Timings = @($timings)
    }
}
$report = [pscustomobject]@{ Session = $Session; Samples = $ordered.Count; SkippedPartialLines = $skipped
    Notes = @('CPU 为本进程按可用逻辑处理器数归一化的百分比；不含 BrowserHost/浏览器。',
        'privateBytes 为私有提交量，workingSetBytes 为包含共享页的总工作集；schema 4 新增 privateWorkingSetBytes，才对应本轮任务管理器内存验收口径。旧日志缺失时返回 null，不以其他指标替代。',
        'gcGenerations 为最近一次 GC 后的分代观测，索引 0–2 为普通代、3 为 LOH、4 为 POH；不是当前时刻的逐对象内存清单。图片读入字节累计值用于分析分配压力，不代表常驻内存。',
        '内存增长率至少 3 个点，仅作定位线索；缓存预热、GC 和原生内存都会影响，不能直接认定泄漏。',
        'timings 为 UI 脉冲间的区间统计；嵌套测量会重叠，不要相加当作 CPU。frameGap 是回调间隔，不是屏幕实际呈现帧率。',
        'schema 3 的 uiImages 内，active、idle、reserved 的显示源及估算字节互斥；reserved 为创建完成、尚未发出租约的资源。',
        'imageResources.nativeSources 含共享源、上传中源及设置预览，是总数；uiImages 为其中共享服务部分，不能相加。像素估算不涵盖 WinUI、驱动和分配器全部原生内存。旧日志缺少的新字段返回 null。',
        'pixelAccounting=backing-plus-surface-v2 时，nativePixelBytesEstimate 与 uiImages 字节预算包含底层位图和一份显示表面估算；两项分量不能再加到总数上，旧版仅计一份像素时不可直接比较。',
        '切换场景前后约一个采样区间可能含过渡活动。原始日志滚动覆盖后只分析保留下来的区间。')
    Phases = @($phases) }
$json = $report | ConvertTo-Json -Depth 12
if ($OutputPath) { [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputPath), $json, [System.Text.UTF8Encoding]::new($false)) }
$json
