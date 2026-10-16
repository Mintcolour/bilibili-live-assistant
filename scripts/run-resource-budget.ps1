param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,47}$')][string]$Name,
    [Parameter(Mandatory)][string]$BinarySource,
    [ValidateRange(60,14400)][int]$Seconds = 3600,
    [ValidateRange(1,2000)][int]$Rate = 3,
    [ValidateSet('steady','intermittent')][string]$Pattern = 'steady',
    [Parameter(Mandatory)][ValidateRange(1,65535)][int]$FixturePort
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = (Resolve-Path -LiteralPath $BinarySource).Path
if (Get-Process -Name StressApp -ErrorAction SilentlyContinue) { throw '已有隔离模拟进程，请先等待其结束，不能并行运行UI对照' }
$calibration = [Collections.Generic.List[string]]::new()
foreach ($index in 1..2) {
    $runName = "$Name-startup-$index"
    $run = Join-Path $repository "artifacts/memory-soak/$runName"
    & (Join-Path $PSScriptRoot 'start-memory-soak.ps1') -Name $runName -Seconds 10 -Rate $Rate -View both -FixturePort $FixturePort -BinarySource $source -StartupOnly
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath (Join-Path $run 'completed.json'))) {
        if (Test-Path -LiteralPath (Join-Path $run 'fatal.txt')) { throw "启动基准失败：$run/fatal.txt" }
        if ($watch.Elapsed.TotalSeconds -gt 120) { throw "启动基准未按时结束：$run" }
        Start-Sleep -Milliseconds 250
    }
    # Completion is written before the dispatcher exits. Wait for the exact
    # isolated process, rather than overlap even the shutdown with the next run.
    $expected = (Resolve-Path -LiteralPath (Join-Path $repository "artifacts/memory-soak/bin-$runName/StressApp.exe")).Path
    $remaining = Get-Process -Name StressApp -ErrorAction SilentlyContinue | Where-Object Path -eq $expected
    if ($remaining) { if (!$remaining.WaitForExit(10000)) { throw '启动基准进程尚未退出' } }
    $calibration.Add($run)
}
& (Join-Path $PSScriptRoot 'start-memory-soak.ps1') -Name $Name -Seconds $Seconds -Rate $Rate -Pattern $Pattern -View both -FixturePort $FixturePort -BinarySource $source
$run = Join-Path $repository "artifacts/memory-soak/$Name"
$samplePath = Join-Path $run 'samples.jsonl'
$watch = [Diagnostics.Stopwatch]::StartNew()
$header = $null
while (!$header) {
    if (Test-Path -LiteralPath $samplePath) {
        $first = Get-Content -LiteralPath $samplePath -TotalCount 1
        if ($first) { try { $header = $first | ConvertFrom-Json } catch { } }
    }
    if (Test-Path -LiteralPath (Join-Path $run 'fatal.txt')) { throw "模拟启动失败：$run/fatal.txt" }
    if ($watch.Elapsed.TotalSeconds -gt 30) { throw '模拟未写入表头' }
    if (!$header) { Start-Sleep -Milliseconds 250 }
}
$binary = Join-Path $repository "artifacts/memory-soak/bin-$Name/StressApp.exe"
& (Join-Path $PSScriptRoot 'measure-process-budget.ps1') -ProcessId $header.pid -ExpectedPath $binary -Output (Join-Path $run 'process-250ms.jsonl') -Seconds ($Seconds + 240)
$calibration.Add($run)
$reportPath = Join-Path $run 'budget-report.json'
& (Join-Path $PSScriptRoot 'analyze-resource-budget.ps1') -Path $run -BaselineRuns ($calibration.ToArray()) -RequiredTrafficSeconds $Seconds |
    Set-Content -LiteralPath $reportPath -Encoding utf8
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
[ordered]@{ report=$reportPath; state=$report.acceptance.state; m0Bytes=$report.startup.m0Bytes; budgetBytes=$report.startup.budgetBytes;
    externalPeakBytes=$report.externalMeter.peakBytes; over160MiBSamples=$report.externalMeter.over160MiBSamples; cpuP95=$report.cpu.p95Percent } | ConvertTo-Json
