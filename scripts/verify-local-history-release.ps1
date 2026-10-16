param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')]
    [string]$ReleaseName = 'local-history',
    [string]$InheritSettingsFrom = 'browser-login-sync-v1'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/releases'))
$release = [IO.Path]::GetFullPath((Join-Path $releaseRoot $ReleaseName))
if (!$release.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '核对目录越界' }
$sourceSettings = [IO.Path]::GetFullPath((Join-Path $releaseRoot "$InheritSettingsFrom/settings.json"))
if (!$sourceSettings.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '设置继承路径越界' }
$r2r = Join-Path $projectRoot 'src/BilibiliLiveAssistant.App/obj/x64/Release/net8.0-windows10.0.19041.0/win-x64/R2R'
foreach ($assembly in @('BilibiliLiveAssistant.App.dll', 'BilibiliLiveAssistant.Core.dll', 'BilibiliLiveAssistant.Infrastructure.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $release $assembly)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $r2r $assembly)).Hash) { throw "Release ReadyToRun 产物不一致：$assembly" }
}
$hostAssembly = 'BilibiliLiveAssistant.BrowserHost.dll'
if ((Get-FileHash -LiteralPath (Join-Path $release "BrowserHost/$hostAssembly")).Hash -ne (Get-FileHash -LiteralPath (Join-Path $projectRoot "src/BilibiliLiveAssistant.BrowserHost/bin/Release/net8.0-windows/win-x64/$hostAssembly")).Hash) { throw 'BrowserHost Release 产物不一致' }
foreach ($relative in @('Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'e_sqlite3.dll', 'BilibiliLiveAssistant.App.pri', 'Assets/app.ico')) {
    if (!(Test-Path -LiteralPath (Join-Path $release $relative) -PathType Leaf)) { throw "缺少 $relative" }
}
$dependency = Get-Content -LiteralPath (Join-Path $release 'BilibiliLiveAssistant.App.deps.json') -Raw | ConvertFrom-Json -AsHashtable
if (!$dependency.libraries.ContainsKey('Microsoft.Data.Sqlite/8.0.32')) { throw 'SQLite 版本与计划不符' }
foreach ($executable in @('BilibiliLiveAssistant.App.exe', 'e_sqlite3.dll', 'BrowserHost/BilibiliLiveAssistant.BrowserHost.exe')) {
    $stream = [IO.File]::OpenRead((Join-Path $release $executable)); $reader = [IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3c; $offset = $reader.ReadInt32(); $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw "PE 不是 AMD64：$executable" }
    } finally { $reader.Dispose(); $stream.Dispose() }
}
$settingsMatched = $false
if (Test-Path -LiteralPath $sourceSettings) {
    if ((Get-FileHash -LiteralPath $sourceSettings).Hash -ne (Get-FileHash -LiteralPath (Join-Path $release 'settings.json')).Hash) { throw '继承设置字节不一致' }
    $settingsMatched = $true
}
$evidence = Join-Path $projectRoot "artifacts/$ReleaseName-verification"
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$manifestTool = Get-ChildItem -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin') -Filter mt.exe -File -Recurse | Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
if (!$manifestTool) { throw '未找到 mt.exe，无法核对内嵌 DPI manifest' }
$manifest = Join-Path $evidence 'app.embedded.manifest'
$manifestArgs = @('-nologo', ('-inputresource:' + (Join-Path $release 'BilibiliLiveAssistant.App.exe') + ';#1'), ('-out:' + $manifest))
& $manifestTool @manifestArgs
if ($LASTEXITCODE -ne 0) { throw '无法提取内嵌 manifest' }
if ((Get-Content -LiteralPath $manifest -Raw) -notmatch 'PerMonitorV2') { throw '发布程序未保留 PerMonitorV2' }
Add-Type -AssemblyName System.IO.Compression.ZipFile
$zipCounts = @{}
foreach ($package in @(@{ File = "$ReleaseName-win-x64.zip"; Folder = $release }, @{ File = "$ReleaseName-extension.zip"; Folder = (Join-Path $release 'browser-extension') })) {
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $releaseRoot $package.File)); $count = 0
    try {
        foreach ($entry in $zip.Entries) {
            if (!$entry.Name) { continue }
            if ($entry.Name -eq 'settings.json' -or $entry.Name -like 'pending-gaps-*' -or $entry.Name -eq 'history.writer.lock' -or $entry.Name -match '\.(db|db-wal|db-shm|dat|tmp|pdb|log|jsonl)$') { throw "ZIP 包含私有或临时文件：$($entry.FullName)" }
            $source = [IO.Path]::GetFullPath((Join-Path $package.Folder $entry.FullName))
            if (!$source.StartsWith($package.Folder + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP 条目越界' }
            $content = $entry.Open(); $hasher = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToHexString($hasher.ComputeHash($content)) } finally { $content.Dispose(); $hasher.Dispose() }
            if ($hash -ne (Get-FileHash -LiteralPath $source).Hash) { throw "ZIP 产物不一致：$($entry.FullName)" }; $count++
        }
    } finally { $zip.Dispose() }
    $zipCounts[$package.File] = $count
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'browser-extension') -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath((Join-Path $projectRoot 'browser-extension'), $file.FullName)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $release "browser-extension/$relative")).Hash) { throw "扩展文件不一致：$relative" }
}
$report = @{
    release = $ReleaseName; sqlite = '8.0.32'; architecture = 'AMD64'; dpi = 'PerMonitorV2'; inheritedSettingsBytesMatched = $settingsMatched
    readyToRunAssembliesMatched = $true; hostReleaseMatched = $true; zipEntries = $zipCounts
    executableSha256 = (Get-FileHash -LiteralPath (Join-Path $release 'BilibiliLiveAssistant.App.exe')).Hash
    startedApplication = $false; ranAutomatedTests = $false
}
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'release-verification.json') -Encoding utf8
$report | ConvertTo-Json -Depth 4
