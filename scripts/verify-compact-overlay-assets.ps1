param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')]
    [string]$ReleaseName = 'compact-list-animation'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/releases'))
$release = [IO.Path]::GetFullPath((Join-Path $releaseRoot $ReleaseName))
if (!$release.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '发布路径越界' }
$resources = @()
$stream = [IO.File]::OpenRead((Join-Path $release 'BilibiliLiveAssistant.App.dll'))
$reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
try {
    $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($reader)
    $block = $reader.GetSectionData($reader.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress)
    foreach ($handle in $metadata.ManifestResources) {
        $resource = $metadata.GetManifestResource($handle)
        $name = $metadata.GetString($resource.Name)
        if (!$name.StartsWith('BilibiliLiveAssistant.App.Obs.', [StringComparison]::Ordinal)) { continue }
        if (!$resource.Implementation.IsNil) { throw "OBS 资源不是内嵌资源：$name" }
        $lengthReader = $block.GetReader([int]$resource.Offset, 4)
        $length = $lengthReader.ReadInt32()
        $dataReader = $block.GetReader(([int]$resource.Offset + 4), $length)
        $content = $dataReader.ReadBytes($length)
        $relative = $name.Substring('BilibiliLiveAssistant.App.Obs.'.Length)
        if ($relative -notin @('index.html', 'overlay.js', 'overlay.css')) { throw "意外 OBS 资源：$name" }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($content))
        if ($hash -ne (Get-FileHash -LiteralPath (Join-Path $repository "src/BilibiliLiveAssistant.App/Obs/$relative")).Hash) { throw "OBS 内嵌内容与源码不一致：$name" }
        $resources += @{ name = $name; sha256 = $hash }
    }
    if ($resources.Count -ne 3) { throw 'OBS 内嵌资源数量不符' }
} finally { $reader.Dispose(); $stream.Dispose() }

Add-Type -AssemblyName System.IO.Compression.ZipFile
$sdkVersion = '1.7.250401001'
$sdkPath = Join-Path $env:USERPROFILE ".nuget/packages/microsoft.windowsappsdk/$sdkVersion/tools/MSIX/win10-x64/Microsoft.WindowsAppRuntime.1.7.msix"
$sdk = [IO.Compression.ZipFile]::OpenRead($sdkPath)
$nativeMatched = @()
try {
    foreach ($name in @('Microsoft.UI.Xaml.dll', 'Microsoft.Internal.FrameworkUdk.dll')) {
        $entry = $sdk.Entries | Where-Object Name -eq $name | Select-Object -First 1
        if (!$entry) { throw "SDK 缺少 $name" }
        $content = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($content)) } finally { $content.Dispose() }
        if ($hash -ne (Get-FileHash -LiteralPath (Join-Path $release $name)).Hash) { throw "WinUI SDK 文件不一致：$name" }
        $nativeMatched += $name
    }
} finally { $sdk.Dispose() }
& node --check (Join-Path $repository 'src/BilibiliLiveAssistant.App/Obs/overlay.js')
if ($LASTEXITCODE -ne 0) { throw 'OBS JavaScript 语法检查失败' }
$report = @{
    release = $ReleaseName; obsEmbeddedResourcesMatched = $resources; winUiSdk = $sdkVersion; sdkNativeMatched = $nativeMatched
    obsJavaScriptSyntax = $true; startedApplication = $false; ranAutomatedTests = $false
    zipSha256 = (Get-FileHash -LiteralPath (Join-Path $releaseRoot "$ReleaseName-win-x64.zip")).Hash
}
$evidence = Join-Path $repository "artifacts/$ReleaseName-verification"
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'supplementary-static-check.json') -Encoding utf8
$report | ConvertTo-Json -Depth 5
