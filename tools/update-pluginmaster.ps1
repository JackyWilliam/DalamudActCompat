param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $false)]
    [string] $SourceRepository = "https://github.com/raynording/DalamudActCompat",

    [Parameter(Mandatory = $false)]
    [string] $PluginMasterPath = "repo/pluginmaster.json",

    [Parameter(Mandatory = $false)]
    [string] $Changelog = "Development build update."
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PluginMasterPath)) {
    throw "PluginMaster file not found: $PluginMasterPath"
}

$downloadUrl = "$SourceRepository/releases/download/v$Version/DalamudActCompat.zip"
$entries = Get-Content $PluginMasterPath -Raw | ConvertFrom-Json
$entry = @($entries)[0]

$entry.AssemblyVersion = "$Version.0"
$entry.DownloadLinkInstall = $downloadUrl
$entry.DownloadLinkUpdate = $downloadUrl
$entry.LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
$entry.Changelog = $Changelog

@($entry) | ConvertTo-Json -Depth 10 | Set-Content -Path $PluginMasterPath -Encoding UTF8

Write-Host "Updated $PluginMasterPath"
Write-Host "Custom repository raw URL:"
Write-Host "https://raw.githubusercontent.com/raynording/DalamudActCompatRepo/main/pluginmaster.json"
