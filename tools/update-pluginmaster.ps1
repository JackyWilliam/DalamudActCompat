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

$versionParts = $Version.Split(".")
if ($versionParts.Count -notin 3, 4 -or
    $versionParts.Where({ $_ -notmatch '^\d+$' }).Count -ne 0) {
    throw "Version must contain three or four numeric parts: $Version"
}
$assemblyVersion = if ($versionParts.Count -eq 3) { "$Version.0" } else { $Version }

if (-not (Test-Path $PluginMasterPath)) {
    throw "PluginMaster file not found: $PluginMasterPath"
}

$downloadUrl = "$SourceRepository/releases/download/v$Version/DalamudActCompat.zip"
$entries = Get-Content $PluginMasterPath -Raw -Encoding UTF8 | ConvertFrom-Json
$entry = @($entries)[0]

$entry.AssemblyVersion = $assemblyVersion
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
