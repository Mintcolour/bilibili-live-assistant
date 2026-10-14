param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')]
    [string]$ReleaseName = 'local-history',
    [string]$InheritSettingsFrom = 'browser-login-sync-v1',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}\.md$')]
    [string]$DeliveryDocument = 'local-history-delivery.md'
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'publish-browser-sync.ps1') -ReleaseName $ReleaseName -InheritSettingsFrom $InheritSettingsFrom -IncludeLocalHistory -LocalHistoryDeliveryDocument $DeliveryDocument
