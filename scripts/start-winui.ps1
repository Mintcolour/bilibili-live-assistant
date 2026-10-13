param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/BilibiliLiveAssistant.App/BilibiliLiveAssistant.App.csproj'
$appPath = Join-Path $repoRoot "src/BilibiliLiveAssistant.App/bin/x64/$Configuration/net8.0-windows10.0.19041.0/win-x64/BilibiliLiveAssistant.App.exe"

if (-not $SkipBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw 'Visual Studio Installer (vswhere.exe) was not found.'
    }
    $msbuild = & $vswhere -latest -prerelease -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/Current/Bin/MSBuild.exe'
    if ($LASTEXITCODE -ne 0 -or -not $msbuild) {
        throw 'Visual Studio MSBuild was not found. Install the Windows App SDK build tools.'
    }
    & ($msbuild | Select-Object -First 1) $project /restore /t:Build "/p:Configuration=$Configuration" /p:Platform=x64 /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
}

if (-not (Test-Path -LiteralPath $appPath)) {
    throw "Application not found: $appPath"
}

# Delegate to the desktop shell so a packaged terminal/tool does not pass its
# package identity to this unpackaged WinUI application.
Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('"{0}"' -f $appPath)
Write-Host "Launch requested via Windows Explorer: $appPath"
