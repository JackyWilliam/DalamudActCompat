param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $false)]
    [string] $SourceRepository = "https://github.com/JackyWilliam/DalamudActCompat",

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
$entries = Get-Content $PluginMasterPath -Raw -Encoding UTF8 | ConvertFrom-Json
$entry = @($entries)[0]

$entry.AssemblyVersion = "$Version.0"
$entry.DownloadLinkInstall = $downloadUrl
$entry.DownloadLinkUpdate = $downloadUrl
$entry.LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$entry.Changelog = $Changelog

$json = $entry | ConvertTo-Json -Depth 10
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($PluginMasterPath, "[`n$json`n]`n", $utf8NoBom)

Write-Host "Updated $PluginMasterPath"
Write-Host "Custom repository raw URL:"
Write-Host "https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json"
