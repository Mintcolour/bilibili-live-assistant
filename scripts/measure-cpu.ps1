param(
    [Parameter(Mandatory)]
    [int]$ProcessId,
    [ValidateRange(5, 600)]
    [int]$Seconds = 30,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$measuredProcess = Get-Process -Id $ProcessId
$logicalProcessors = [Environment]::ProcessorCount
$sampleClock = [Diagnostics.Stopwatch]::StartNew()
$previousCpu = $measuredProcess.TotalProcessorTime.TotalSeconds
$previousTime = $sampleClock.Elapsed.TotalSeconds
$samples = for ($index = 0; $index -lt $Seconds; $index++) {
    Start-Sleep -Milliseconds 1000
    $measuredProcess.Refresh()
    if ($measuredProcess.HasExited) { throw 'The measured process exited.' }
    $nowCpu = $measuredProcess.TotalProcessorTime.TotalSeconds
    $nowTime = $sampleClock.Elapsed.TotalSeconds
    [pscustomobject]@{
        CpuPercent = [Math]::Round(100 * ($nowCpu - $previousCpu) / ($nowTime - $previousTime) / $logicalProcessors, 3)
        WorkingSetMB = [Math]::Round($measuredProcess.WorkingSet64 / 1MB, 1)
    }
    $previousCpu = $nowCpu
    $previousTime = $nowTime
}
$sortedCpu = @($samples.CpuPercent | Sort-Object)
$report = [pscustomobject]@{
    ProcessId = $ProcessId
    Seconds = $Seconds
    LogicalProcessors = $logicalProcessors
    AverageCpuPercent = [Math]::Round(($samples.CpuPercent | Measure-Object -Average).Average, 3)
    P95CpuPercent = $sortedCpu[[Math]::Ceiling($sortedCpu.Count * 0.95) - 1]
    PeakCpuPercent = $sortedCpu[-1]
    Samples = $samples
}
$json = $report | ConvertTo-Json -Depth 4
if ($OutputPath) { Set-Content -LiteralPath $OutputPath -Value $json -Encoding utf8 }
$report | Select-Object ProcessId, Seconds, LogicalProcessors, AverageCpuPercent, P95CpuPercent, PeakCpuPercent
