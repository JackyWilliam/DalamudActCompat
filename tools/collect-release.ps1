param(
    [Parameter(Mandatory = $false)]
    [string] $Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string] $OutputDirectory = "artifacts/release",

    [Parameter(Mandatory = $false)]
    [string] $ExpectedAssemblyVersion = "0.1.7.0",

    [Parameter(Mandatory = $false)]
    [int] $ExpectedDalamudApiLevel = 15,

    [Parameter(Mandatory = $false)]
    [bool] $RequireCompatibilityHost = $true
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

if ($RequireCompatibilityHost) {
    $hostExePath = Join-Path $validationDir "host/DalamudActCompat.Host.exe"
    if (-not (Test-Path $hostExePath)) {
        throw "Plugin ZIP does not contain host/DalamudActCompat.Host.exe. Rebuild after confirming the Compatibility Host project built successfully."
    }

    Write-Host "Validated Compatibility Host: host/DalamudActCompat.Host.exe"

    $pluginAssemblyPath = Join-Path $validationDir "DalamudActCompat.dll"
    $pluginAssembly = [System.Reflection.Assembly]::LoadFile($pluginAssemblyPath)
    $hostResources = @($pluginAssembly.GetManifestResourceNames() | Where-Object {
        $_.StartsWith("DalamudActCompat.HostAssets.", [System.StringComparison]::Ordinal)
    })
    $requiredHostResources = @(
        "DalamudActCompat.HostAssets.DalamudActCompat.Host.exe",
        "DalamudActCompat.HostAssets.DalamudActCompat.Host.dll",
        "DalamudActCompat.HostAssets.DalamudActCompat.Host.deps.json",
        "DalamudActCompat.HostAssets.DalamudActCompat.Host.runtimeconfig.json"
    )
    $missingHostResources = @($requiredHostResources | Where-Object { $_ -notin $hostResources })
    if ($missingHostResources.Count -gt 0) {
        throw "Plugin assembly is missing embedded Compatibility Host resources: $($missingHostResources -join ', ')"
    }

    Write-Host "Validated embedded Compatibility Host resources: $($requiredHostResources.Count)"
}

$requiredRuntimeFiles = @(
    "Advanced Combat Tracker.dll",
    "DalamudActCompat.ActRuntime.dll",
    "FFXIV_ACT_Plugin.dll",
    "FFXIV_ACT_Plugin.Common.dll",
    "FFXIV_ACT_Plugin.Config.dll",
    "FFXIV_ACT_Plugin.Logfile.dll",
    "FFXIV_ACT_Plugin.Memory.dll",
    "FFXIV_ACT_Plugin.Network.dll",
    "FFXIV_ACT_Plugin.Parse.dll",
    "FFXIV_ACT_Plugin.Resource.dll",
    "OverlayPlugin.Common.dll",
    "OverlayPlugin.Core.dll",
    "LICENSE.md",
    "LICENSES/IINACT-GPL-3.0.txt",
    "LICENSES/OverlayPlugin.Core-LICENSE.txt",
    "THIRD_PARTY_NOTICES.md"
)
$missingRuntimeFiles = @($requiredRuntimeFiles | Where-Object {
    -not (Test-Path (Join-Path $validationDir $_))
})
if ($missingRuntimeFiles.Count -gt 0) {
    throw "Plugin ZIP is missing self-hosted ACT runtime files: $($missingRuntimeFiles -join ', ')"
}

Write-Host "Validated self-hosted ACT runtime files: $($requiredRuntimeFiles.Count)"

Write-Host "Collected plugin ZIP: $destination"
Write-Host "Validated AssemblyVersion: $($manifest.AssemblyVersion)"
Write-Host "Validated DalamudApiLevel: $($manifest.DalamudApiLevel)"
