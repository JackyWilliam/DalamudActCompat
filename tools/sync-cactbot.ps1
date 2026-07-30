param(
    [Parameter(Mandatory = $false)]
    [switch] $Check
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$destinationRoot = Join-Path $projectRoot "vendor/BundledCactbot"
$lockPath = Join-Path $destinationRoot "bundled-cactbot.lock.json"
$headers = @{
    "Accept" = "application/vnd.github+json"
    "User-Agent" = "DalamudActCompat Cactbot sync"
}

$release = Invoke-RestMethod `
    -Uri "https://api.github.com/repos/OverlayPlugin/cactbot/releases/latest" `
    -Headers $headers
$asset = @($release.assets | Where-Object { $_.name -match "^cactbot-[0-9.]+\.zip$" })
if ($asset.Count -ne 1) {
    throw "Expected one official Cactbot release ZIP, found $($asset.Count)."
}

$version = ([string] $release.tag_name).TrimStart("v")
$archiveName = [string] $asset[0].name
$archivePath = Join-Path $destinationRoot $archiveName
$expectedDigest = ([string] $asset[0].digest).Replace("sha256:", "").ToLowerInvariant()
$licenseUrl = "https://raw.githubusercontent.com/OverlayPlugin/cactbot/$($release.tag_name)/LICENSE"
$licensePath = Join-Path $destinationRoot "LICENSE.txt"

if ($Check) {
    if (-not (Test-Path $lockPath)) {
        throw "Bundled Cactbot lock file is missing: $lockPath"
    }

    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    if ($lock.version -ne $version -or
        $lock.downloadUrl -ne [string] $asset[0].browser_download_url -or
        $lock.relativeArchive -ne $archiveName -or
        $lock.sha256 -ne $expectedDigest) {
        throw "Bundled Cactbot is not current with official release $($release.tag_name)."
    }

    if (-not (Test-Path $archivePath) -or -not (Test-Path $licensePath)) {
        throw "Bundled Cactbot archive or license is missing."
    }

    $actualDigest = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
    if ($actualDigest -ne $expectedDigest) {
        throw "Bundled Cactbot archive SHA-256 does not match the official release."
    }

    Write-Host "Bundled Cactbot $version is current."
    exit 0
}

New-Item -ItemType Directory -Force -Path $destinationRoot | Out-Null
Invoke-WebRequest -Uri $asset[0].browser_download_url -Headers $headers -OutFile $archivePath
Invoke-WebRequest -Uri $licenseUrl -Headers $headers -OutFile $licensePath

$actualDigest = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
if ($actualDigest -ne $expectedDigest) {
    throw "Downloaded Cactbot archive SHA-256 does not match the official release."
}

$lock = [ordered] @{
    schemaVersion = 1
    version = $version
    projectUrl = "https://github.com/OverlayPlugin/cactbot"
    downloadUrl = [string] $asset[0].browser_download_url
    relativeArchive = $archiveName
    sha256 = $actualDigest
}
$lock | ConvertTo-Json | Set-Content -LiteralPath $lockPath -Encoding utf8
Write-Host "Synchronized bundled Cactbot $version."
