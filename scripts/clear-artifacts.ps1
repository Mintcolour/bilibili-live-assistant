param(
    [ValidateRange(0, 365)][int]$KeepDays = 2,
    [ValidateRange(1, 100)][int]$KeepExperiments = 3,
    [ValidateRange(1, 100)][int]$KeepReleases = 2,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$soakRoot = Join-Path $artifactRoot 'memory-soak'
$releaseRoot = Join-Path $artifactRoot 'releases'
$cutoff = (Get-Date).AddDays(-$KeepDays)

# 发布产物是自包含的 MSIX 运行时拷贝，每次实验 300 MB 量级；运行记录（analysis.json /
# samples.jsonl / images）才是证据，本脚本只清构建产物，不碰运行记录。
function Get-TreeBytes {
    param([string]$Path)
    $entry = Get-Item -LiteralPath $Path -Force
    if ($entry.PSIsContainer) {
        return [double](Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
            Measure-Object Length -Sum).Sum
    }
    return [double]$entry.Length
}

function Assert-InsideArtifacts {
    param([string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理 artifacts 之外的路径：$full"
    }
    $entry = Get-Item -LiteralPath $full -Force
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "拒绝清理重解析点：$full" }
    return $full
}

function Get-InUseDirectories {
    # 正在运行的程序所在的 release 目录必须保留，否则会删掉用户当前打开的版本。
    $paths = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Split-Path -Parent $_.Path }
    return @($paths)
}

$inUse = Get-InUseDirectories
$plan = [Collections.Generic.List[object]]::new()

if (Test-Path -LiteralPath $soakRoot) {
    $publishDirs = Get-ChildItem -LiteralPath $soakRoot -Directory -Force |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'StressApp.exe') } |
        Sort-Object LastWriteTime -Descending
    foreach ($directory in $publishDirs) {
        $rank = [array]::IndexOf(@($publishDirs | ForEach-Object Name), $directory.Name)
        $isRecentExperiment = $rank -lt ($KeepExperiments * 2)
        if ($isRecentExperiment -or $directory.LastWriteTime -ge $cutoff) { continue }
        $plan.Add([pscustomobject]@{ kind = 'soak-build'; path = $directory.FullName; bytes = Get-TreeBytes $directory.FullName })
    }
}

if (Test-Path -LiteralPath $releaseRoot) {
    $releases = Get-ChildItem -LiteralPath $releaseRoot -Directory -Force | Sort-Object LastWriteTime -Descending
    $keptNames = @($releases | Select-Object -First $KeepReleases | ForEach-Object Name)
    foreach ($directory in $releases) {
        $protected = $keptNames -contains $directory.Name -or $inUse -contains $directory.FullName
        if ($protected) { continue }
        $plan.Add([pscustomobject]@{ kind = 'release'; path = $directory.FullName; bytes = Get-TreeBytes $directory.FullName })
    }
    foreach ($archive in Get-ChildItem -LiteralPath $releaseRoot -File -Force -Filter *.zip) {
        $owned = $false
        foreach ($name in $keptNames) { if ($archive.Name.StartsWith($name + '-', [StringComparison]::OrdinalIgnoreCase)) { $owned = $true } }
        if ($owned) { continue }
        $plan.Add([pscustomobject]@{ kind = 'release-archive'; path = $archive.FullName; bytes = [double]$archive.Length })
    }
}

# 符号缓存与失败下载只服务一次排查，随时可重新获取。
foreach ($pattern in @('symbol-cache', 'symcache', 'symbols-local', 'plot-packages', 'debugger-tools-x64')) {
    foreach ($directory in Get-ChildItem -LiteralPath $artifactRoot -Directory -Force -Filter 'live-memory-*') {
        $candidate = Join-Path $directory.FullName $pattern
        if (!(Test-Path -LiteralPath $candidate)) { continue }
        if ((Get-Item -LiteralPath $candidate -Force).LastWriteTime -ge $cutoff) { continue }
        $plan.Add([pscustomobject]@{ kind = 'symbol-cache'; path = $candidate; bytes = Get-TreeBytes $candidate })
    }
}
foreach ($directory in Get-ChildItem -LiteralPath $artifactRoot -Directory -Force) {
    foreach ($failed in Get-ChildItem -LiteralPath $directory.FullName -File -Force -Filter 'download*.error' -ErrorAction SilentlyContinue) {
        $plan.Add([pscustomobject]@{ kind = 'failed-download'; path = $failed.FullName; bytes = [double]$failed.Length })
    }
}

$plannedBytes = ($plan | Measure-Object -Property bytes -Sum).Sum
if ($plan.Count -eq 0) {
    [ordered]@{ executed = $Execute.IsPresent; deleted = 0; freedBytes = 0; message = '没有需要清理的构建产物' } | ConvertTo-Json
    return
}
$plan | Sort-Object { $_.bytes } -Descending |
    Select-Object @{ n = '类型'; e = { $_.kind } }, @{ n = '大小MB'; e = { [math]::Round($_.bytes / 1MB, 1) } }, @{ n = '路径'; e = { $_.path.Replace($repository + [IO.Path]::DirectorySeparatorChar, '') } } |
    Format-Table -AutoSize
"计划清理 {0} 项，合计 {1:N2} GB" -f $plan.Count, ($plannedBytes / 1GB)

if (!$Execute) {
    '当前为演练模式；确认无误后加 -Execute 实际执行。' | Write-Host
    [ordered]@{ executed = $false; candidates = $plan.Count; freedBytes = $plannedBytes } | ConvertTo-Json
    return
}

$deleted = 0
$freed = 0.0
foreach ($item in $plan) {
    $path = Assert-InsideArtifacts $item.path
    $entry = Get-Item -LiteralPath $path -Force
    if ($entry.PSIsContainer) { [IO.Directory]::Delete($path, $true) } else { [IO.File]::Delete($path) }
    $deleted++
    $freed += $item.bytes
}
[ordered]@{ executed = $true; deleted = $deleted; freedBytes = $freed; freedGB = [math]::Round($freed / 1GB, 2) } | ConvertTo-Json
