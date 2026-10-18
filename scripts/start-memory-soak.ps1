param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$Name,
    [ValidateRange(10,14400)][int]$Seconds = 180,
    [ValidateRange(1,2000)][int]$Rate = 50,
    [ValidateRange(1,1000000)][int]$Users = 20000,
    [ValidateSet('both','overlay','dashboard','pipeline')][string]$View = 'both',
    [ValidateSet('unique','shared','none','avatars','emotes')][string]$Images = 'unique',
    [string]$BinarySource = '',
    [string]$AppearanceSettingsPath = '',
    [switch]$UseCurrentAccount,
    [switch]$KeepOverviewVisible,
    [ValidateRange(0,600)][int]$StopObservationSeconds = 0,
    [switch]$Snapshot,
    [switch]$NoImageBinding,
    [switch]$NoImageSources,
    [switch]$DrawingDecoder,
    [switch]$ScrollProbe,
    [ValidateRange(0,2147483647)][int]$LiveRoom = 0,
    [ValidateRange(0,120)][int]$BaselineSeconds = 60,
    [switch]$NoEntryAvatars,
    [ValidateRange(0,65535)][int]$FixturePort = 0,
    [ValidateRange(1,10000)][int]$FixtureCount = 11,
    [ValidateSet('steady','intermittent')][string]$Pattern = 'steady',
    [switch]$StartupOnly,
    [switch]$LifecycleProbe,
    [switch]$AnimateEmotes,
    [switch]$InteractionProbe,
    [switch]$TradeProbe,
    [switch]$PlaybackProbe
)
$ErrorActionPreference = 'Stop'
if ($UseCurrentAccount -and $LiveRoom -le 0) { throw '当前账号仅允许用于明确指定的真实房间观察' }
if ($KeepOverviewVisible -and $View -notin @('both','dashboard')) { throw '保持概览可见仅适用于概览显示场景' }
if ($StopObservationSeconds -gt 0 -and $LiveRoom -le 0) { throw '停止后观察目前仅适用于真实房间' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceDirectory = Join-Path $repository 'tests/BilibiliLiveAssistant.StressHarness/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64'
if ($BinarySource.Length -gt 0) { $sourceDirectory = (Resolve-Path -LiteralPath $BinarySource).Path }
$runDirectory = Join-Path $repository "artifacts/memory-soak/$Name"
$binaryDirectory = Join-Path $repository "artifacts/memory-soak/bin-$Name"
if ((Test-Path -LiteralPath $runDirectory) -or (Test-Path -LiteralPath $binaryDirectory)) { throw '测试目录已存在，请使用新的 Name，避免覆盖证据' }
if (!(Test-Path -LiteralPath (Join-Path $sourceDirectory 'StressApp.exe'))) { throw '请先编译 Release x64 压力程序' }
New-Item -ItemType Directory -Path $runDirectory,$binaryDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $sourceDirectory -Force | Copy-Item -Destination $binaryDirectory -Recurse
[ordered]@{ Output=$runDirectory; Name=$Name; Seconds=$Seconds; Rate=$Rate; Users=$Users; WarmupSeconds=10; View=$View; Snapshot=$Snapshot.IsPresent; Images=$Images; AttachImages=!$NoImageBinding.IsPresent; CreateSources=!$NoImageSources.IsPresent; DrawingDecode=$DrawingDecoder.IsPresent; ScrollProbe=$ScrollProbe.IsPresent; LiveRoom=$LiveRoom; BaselineSeconds=$BaselineSeconds; EntryAvatars=!$NoEntryAvatars.IsPresent; FixturePort=$FixturePort; FixtureCount=$FixtureCount; Pattern=$Pattern; StartupOnly=$StartupOnly.IsPresent; LifecycleProbe=$LifecycleProbe.IsPresent; AnimateEmotes=$AnimateEmotes.IsPresent } |
    ForEach-Object { $_.InteractionProbe = $InteractionProbe.IsPresent; $_.TradeProbe = $TradeProbe.IsPresent; $_.PlaybackProbe = $PlaybackProbe.IsPresent;
        $_.AppearanceSettingsPath = if ($AppearanceSettingsPath.Length -gt 0) { (Resolve-Path -LiteralPath $AppearanceSettingsPath).Path } else { '' };
        $_.KeepOverviewVisible = $KeepOverviewVisible.IsPresent; $_.StopObservationSeconds = $StopObservationSeconds; $_.UseCurrentAccount = $UseCurrentAccount.IsPresent; $_ } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $binaryDirectory 'soak-config.json') -Encoding utf8
$binary = Join-Path $binaryDirectory 'StressApp.exe'
# Explorer avoids inheriting the Codex/terminal package identity. Never launch
# the real assistant, BrowserHost or any browser account listener for this run.
Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('"{0}"' -f $binary) -WindowStyle Hidden
[ordered]@{ launchRequested=$true; results=$runDirectory; binary=$binary; seconds=$Seconds; rate=$Rate } | ConvertTo-Json
