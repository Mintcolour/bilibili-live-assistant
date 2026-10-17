param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')]
    [string]$ReleaseName = 'compact-overlay-obs',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}\.md$')]
    [string]$DeliveryDocument = 'compact-overlay-obs-delivery.md'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/releases'))
$release = [IO.Path]::GetFullPath((Join-Path $releaseRoot $ReleaseName))
$archive = [IO.Path]::GetFullPath((Join-Path $releaseRoot "$ReleaseName-win-x64.zip"))
$temporary = [IO.Path]::GetFullPath((Join-Path $releaseRoot "$ReleaseName-win-x64.finalizing.zip"))
foreach ($target in @($release, $archive, $temporary)) {
    if (!$target.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '发布路径越界' }
}
if (!(Test-Path -LiteralPath (Join-Path $release 'BilibiliLiveAssistant.App.exe'))) { throw "请先发布 $ReleaseName" }
if (Test-Path -LiteralPath $temporary) { throw '临时包已存在，拒绝覆盖' }
Copy-Item -LiteralPath (Join-Path $repository "docs/$DeliveryDocument") -Destination (Join-Path $release '交付核对.md')
Copy-Item -LiteralPath (Join-Path $repository 'docs/compact-overlay-obs-delivery.md') -Destination (Join-Path $release '紧凑浮窗与OBS说明.md')
$toolsDirectory = Join-Path $release 'tools'
New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'scripts/analyze-performance.ps1') -Destination (Join-Path $toolsDirectory 'analyze-performance.ps1')
# Every compact release retains the available OBS CSS documentation and examples.
if (Test-Path -LiteralPath (Join-Path $repository 'docs/obs-css-themes.md')) {
    Copy-Item -LiteralPath (Join-Path $repository 'docs/obs-css-themes.md') -Destination (Join-Path $release 'OBS样式定制说明.md')
    $examples = Join-Path $release 'OBS样式示例'
    New-Item -ItemType Directory -Path $examples -Force | Out-Null
    foreach ($example in Get-ChildItem -LiteralPath (Join-Path $repository 'docs/obs-css-examples') -File -Filter '*.css') {
        Copy-Item -LiteralPath $example.FullName -Destination (Join-Path $examples $example.Name)
    }
}
Add-Type -AssemblyName System.IO.Compression.ZipFile
$output = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew)
$zip = [IO.Compression.ZipArchive]::new($output, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $release -File -Recurse) {
        if ($file.Name -in @('settings.json', 'history.writer.lock', 'obs-source.key') -or $file.Name -like 'pending-gaps-*' -or
            $file.Extension -in @('.pdb', '.tmp', '.dat', '.db', '.db-wal', '.db-shm', '.log', '.jsonl')) { continue }
        $relative = [IO.Path]::GetRelativePath($release, $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose(); $output.Dispose() }
$zip = [IO.Compression.ZipFile]::OpenRead($temporary)
try {
    foreach ($entry in $zip.Entries) {
        if (!$entry.Name) { continue }
        if ($entry.Name -in @('settings.json', 'obs-source.key', 'history.writer.lock') -or $entry.Name -like 'pending-gaps-*' -or
            $entry.Name -match '\.(pdb|tmp|dat|db|db-wal|db-shm|log|jsonl)$') { throw '分享包包含私有文件' }
        $source = [IO.Path]::GetFullPath((Join-Path $release $entry.FullName))
        if (!$source.StartsWith($release + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP 路径越界' }
        $content = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($content)) } finally { $content.Dispose() }
        if ($hash -ne (Get-FileHash -LiteralPath $source).Hash) { throw "包内文件不一致：$($entry.FullName)" }
    }
    $count = $zip.Entries.Count
} finally { $zip.Dispose() }
Move-Item -LiteralPath $temporary -Destination $archive -Force
[ordered]@{ package = $archive; entries = $count; sha256 = (Get-FileHash -LiteralPath $archive).Hash; bytes = (Get-Item -LiteralPath $archive).Length } | ConvertTo-Json
