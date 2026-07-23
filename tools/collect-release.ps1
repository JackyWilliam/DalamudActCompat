param(
    [Parameter(Mandatory = $false)]
    [string] $Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string] $OutputDirectory = "artifacts/release",

    [Parameter(Mandatory = $false)]
    [string] $ExpectedAssemblyVersion = "0.1.1.0",

    [Parameter(Mandatory = $false)]
    [int] $ExpectedDalamudApiLevel = 15
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$pluginProject = Join-Path $projectRoot "src/DalamudActCompat"
$outputPath = Join-Path $projectRoot $OutputDirectory

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$zip = Get-ChildItem -Path $pluginProject -Recurse -Filter "DalamudActCompat.zip" |
    Where-Object { $_.FullName -match [Regex]::Escape($Configuration) } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if ($null -eq $zip) {
    $zip = Get-ChildItem -Path $pluginProject -Recurse -Filter "*.zip" |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

if ($null -eq $zip) {
    throw "No plugin ZIP was found under $pluginProject. Confirm DalamudPackager output after dotnet build."
}

$destination = Join-Path $outputPath "DalamudActCompat.zip"
Copy-Item -Path $zip.FullName -Destination $destination -Force

$validationDir = Join-Path $outputPath "validation"
if (Test-Path $validationDir) {
    Remove-Item -Path $validationDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $validationDir | Out-Null
Expand-Archive -Path $destination -DestinationPath $validationDir -Force

$manifestPath = Join-Path $validationDir "DalamudActCompat.json"
if (-not (Test-Path $manifestPath)) {
    throw "Plugin ZIP does not contain DalamudActCompat.json."
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.AssemblyVersion -ne $ExpectedAssemblyVersion) {
    throw "Plugin ZIP AssemblyVersion mismatch. Expected $ExpectedAssemblyVersion, got $($manifest.AssemblyVersion). Rebuild the plugin before publishing."
}

if ([int]$manifest.DalamudApiLevel -ne $ExpectedDalamudApiLevel) {
    throw "Plugin ZIP DalamudApiLevel mismatch. Expected $ExpectedDalamudApiLevel, got $($manifest.DalamudApiLevel). Rebuild the plugin before publishing."
}

Write-Host "Collected plugin ZIP: $destination"
Write-Host "Validated AssemblyVersion: $($manifest.AssemblyVersion)"
Write-Host "Validated DalamudApiLevel: $($manifest.DalamudApiLevel)"
