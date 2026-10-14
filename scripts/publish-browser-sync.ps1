param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')]
    [string]$ReleaseName = 'browser-login-sync-v1',
    [string]$InheritSettingsFrom = 'media-cards'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/releases'))
$destination = [IO.Path]::GetFullPath((Join-Path $releaseRoot $ReleaseName))
if (!$destination.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '发布路径超出 releases' }
if ((Test-Path -LiteralPath $destination) -and (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)) { throw '发布目录非空，请使用新的 ReleaseName' }
$previousSettings = [IO.Path]::GetFullPath((Join-Path $releaseRoot "$InheritSettingsFrom/settings.json"))
if (!$previousSettings.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '继承路径超出 releases' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$buildExe = (& $vswherePath -latest -prerelease -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/Current/Bin/MSBuild.exe' | Select-Object -First 1)
if (!$buildExe) { throw '未找到 Visual Studio MSBuild' }
$buildArgs = @(
    (Join-Path $projectRoot 'src/BilibiliLiveAssistant.App/BilibiliLiveAssistant.App.csproj'),
    '/restore', '/t:Publish', '/p:PublishProfile=win-x64', '/p:Configuration=Release', '/p:Platform=x64',
    "/p:PublishDir=$destination/", '/v:minimal'
)
& $buildExe @buildArgs
if ($LASTEXITCODE -ne 0) { throw '桌面 Release 发布失败' }
$bridgeDirectory = Join-Path $destination 'BrowserHost'
& dotnet publish (Join-Path $projectRoot 'src/BilibiliLiveAssistant.BrowserHost/BilibiliLiveAssistant.BrowserHost.csproj') -c Release -r win-x64 --self-contained true -o $bridgeDirectory
if ($LASTEXITCODE -ne 0) { throw '桥接 Release 发布失败' }
[IO.File]::WriteAllText((Join-Path $bridgeDirectory 'protocol-version.txt'), '1', [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $projectRoot 'browser-extension') -Destination $destination -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/browser-login-sync.md') -Destination (Join-Path $destination '浏览器登录同步说明.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/browser-login-sync-delivery.md') -Destination (Join-Path $destination '交付核对.md')
# Only the user's private local directory inherits settings. Distributable ZIPs exclude them.
if (Test-Path -LiteralPath $previousSettings) { Copy-Item -LiteralPath $previousSettings -Destination (Join-Path $destination 'settings.json') }
$required = @('BilibiliLiveAssistant.App.exe', 'BilibiliLiveAssistant.App.dll', 'BilibiliLiveAssistant.App.pri', 'coreclr.dll', 'hostfxr.dll',
    'Microsoft.UI.Xaml.dll', 'BrowserHost/BilibiliLiveAssistant.BrowserHost.exe', 'BrowserHost/BilibiliLiveAssistant.BrowserHost.dll',
    'BrowserHost/coreclr.dll', 'BrowserHost/hostfxr.dll', 'BrowserHost/protocol-version.txt', 'browser-extension/manifest.json', 'browser-extension/background.js')
foreach ($relative in $required) { if (!(Test-Path -LiteralPath (Join-Path $destination $relative) -PathType Leaf)) { throw "发布缺少 $relative" } }
foreach ($script in @('background.js', 'popup.js')) {
    & node --check (Join-Path $destination "browser-extension/$script")
    if ($LASTEXITCODE -ne 0) { throw "扩展语法检查失败：$script" }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.ZipFile
function Write-Package([string]$folder, [string]$zipPath) {
    $fileStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
    $archive = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $folder -File -Recurse) {
            if ($file.Name -eq 'settings.json' -or $file.Extension -in @('.pdb', '.tmp', '.dat')) { continue }
            $relative = [IO.Path]::GetRelativePath($folder, $file.FullName).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose(); $fileStream.Dispose() }
}
Write-Package (Join-Path $destination 'browser-extension') (Join-Path $releaseRoot "$ReleaseName-extension.zip")
Write-Package $destination (Join-Path $releaseRoot "$ReleaseName-win-x64.zip")
Write-Output "已发布：$destination（未启动软件、安装组件或读取浏览器状态）"
