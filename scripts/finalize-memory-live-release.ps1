$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = Join-Path $repository 'artifacts/releases'
$release = [IO.Path]::GetFullPath((Join-Path $releaseRoot 'memory-live-soak'))
$archivePath = [IO.Path]::GetFullPath((Join-Path $releaseRoot 'memory-live-soak-win-x64.zip'))
$temporaryArchive = [IO.Path]::GetFullPath((Join-Path $releaseRoot 'memory-live-soak-win-x64.finalizing.zip'))
foreach ($target in @($release, $archivePath, $temporaryArchive)) {
    if (!$target.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '发布路径越界' }
}
if (!(Test-Path -LiteralPath (Join-Path $release 'BilibiliLiveAssistant.App.exe'))) { throw '请先完成 memory-live-soak 发布' }
if (Test-Path -LiteralPath $temporaryArchive) { throw '上次临时包仍在，拒绝覆盖' }
$documents = @('memory-live-soak-delivery.md', 'memory-live-soak-progress.md', 'memory-live-soak-release-verification.md')
foreach ($document in $documents) {
    Copy-Item -LiteralPath (Join-Path $repository "docs/$document") -Destination (Join-Path $release $document)
}
Copy-Item -LiteralPath (Join-Path $repository 'docs/memory-live-soak-delivery.md') -Destination (Join-Path $release '交付核对.md')

# Build a new archive before replacing the unpublished package. No user settings,
# database, performance logs or temporary files enter the distributable archive.
$stream = [IO.File]::Open($temporaryArchive, [IO.FileMode]::CreateNew)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $release -File -Recurse) {
        if ($file.Name -eq 'settings.json' -or $file.Name -like 'pending-gaps-*' -or $file.Name -eq 'history.writer.lock' -or
            $file.Extension -in @('.pdb', '.tmp', '.dat', '.db', '.db-wal', '.db-shm', '.log', '.jsonl')) { continue }
        $relative = [IO.Path]::GetRelativePath($release, $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose(); $stream.Dispose() }

$zip = [IO.Compression.ZipFile]::OpenRead($temporaryArchive)
try {
    foreach ($entry in $zip.Entries) {
        if (!$entry.Name) { continue }
        if ($entry.Name -eq 'settings.json' -or $entry.Name -like 'pending-gaps-*' -or $entry.Name -eq 'history.writer.lock' -or
            $entry.Name -match '\.(pdb|tmp|dat|db|db-wal|db-shm|log|jsonl)$') { throw '分享包包含私有或临时文件' }
        $source = [IO.Path]::GetFullPath((Join-Path $release $entry.FullName))
        if (!$source.StartsWith($release + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP 条目越界' }
        $content = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($content)) }
        finally { $content.Dispose() }
        if ($hash -ne (Get-FileHash -LiteralPath $source).Hash) { throw "包内文件不一致：$($entry.FullName)" }
    }
    $entries = $zip.Entries.Count
} finally { $zip.Dispose() }
Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
[ordered]@{ archive=$archivePath; entries=$entries; documentsIncluded=$documents; privateFilesExcluded=$true } | ConvertTo-Json
