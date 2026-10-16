param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$Name,
    [ValidatePattern('^1\.7\.\d+$')][string]$AppSdkVersion = '1.7.250401001',
    [string]$SourceRoot = ''
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectDirectory = Join-Path $repository 'tests/BilibiliLiveAssistant.StressHarness'
$publishDirectory = Join-Path $repository "artifacts/memory-soak/build-$Name"
if (Test-Path -LiteralPath $publishDirectory) { throw '构建目录已存在，请使用新的 Name' }
# The SDK caches extracted MSIX native DLLs below obj. A package-version
# change can otherwise publish the previous runtime even after restore.
foreach ($generatedName in @('bin','obj')) {
    $generatedPath = [IO.Path]::GetFullPath((Join-Path $projectDirectory $generatedName))
    if ($generatedPath -ne (Join-Path $projectDirectory $generatedName) -or
        !$generatedPath.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '生成目录不在预期的压力项目内'
    }
    if (Test-Path -LiteralPath $generatedPath) {
        $entry = Get-Item -LiteralPath $generatedPath
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '拒绝清理重解析目录' }
        Remove-Item -LiteralPath $generatedPath -Recurse -Force
    }
}
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$buildExe = & $vswherePath -latest -prerelease -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/Current/Bin/MSBuild.exe' | Select-Object -First 1
if (!$buildExe) { throw '未找到 Visual Studio MSBuild' }
$buildArguments = @(
    (Join-Path $projectDirectory 'StressApp.csproj'), '/restore', '/t:Publish', '/p:Configuration=Release',
    '/p:Platform=x64', '/p:PublishReadyToRun=true', '/p:SelfContained=true',
    "/p:AppSdkVersion=$AppSdkVersion", "/p:PublishDir=$publishDirectory/", '/v:minimal'
)
if ($SourceRoot.Length -gt 0) { $buildArguments += "/p:SourceRoot=$((Resolve-Path -LiteralPath $SourceRoot).Path)" }
& $buildExe @buildArguments
if ($LASTEXITCODE -ne 0) { throw '压力程序发布失败' }
$assets = Get-Content -LiteralPath (Join-Path $projectDirectory 'obj/project.assets.json') -Raw | ConvertFrom-Json
$package = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
    Join-Path $_ "microsoft.windowsappsdk/$AppSdkVersion/tools/MSIX/win10-x64/Microsoft.WindowsAppRuntime.1.7.msix"
} | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$package) { throw '未找到 SDK 原生运行时包，无法核对构建' }
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $nativeModules = foreach ($moduleName in @('Microsoft.ui.xaml.dll','Microsoft.UI.Xaml.Controls.dll')) {
        $entry = $zip.Entries | Where-Object FullName -ieq $moduleName | Select-Object -First 1
        if (!$entry) { throw "运行时包缺少 $moduleName" }
        $stream = $entry.Open()
        try { $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        $actual = (Get-FileHash -LiteralPath (Join-Path $publishDirectory $moduleName) -Algorithm SHA256).Hash
        if ($expected -ne $actual) { throw "$moduleName 与指定 SDK 不一致，禁止作为验收构建" }
        [ordered]@{ name=$moduleName; sha256=$actual }
    }
}
finally { $zip.Dispose() }
[ordered]@{ appSdkVersion=$AppSdkVersion; nativeModules=@($nativeModules); alternateSource=($SourceRoot.Length -gt 0);
    appSha256=(Get-FileHash -LiteralPath (Join-Path $publishDirectory 'StressApp.dll') -Algorithm SHA256).Hash } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $publishDirectory 'soak-build.json') -Encoding utf8
[ordered]@{ verified=$true; directory=$publishDirectory; appSdkVersion=$AppSdkVersion } | ConvertTo-Json
