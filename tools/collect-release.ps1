param(
    [Parameter(Mandatory = $false)]
    [string] $Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string] $OutputDirectory = "artifacts/release",

    [Parameter(Mandatory = $false)]
    [string] $ExpectedAssemblyVersion = "0.3.6.13",

    [Parameter(Mandatory = $false)]
    [int] $ExpectedDalamudApiLevel = 15,

    [Parameter(Mandatory = $false)]
    [bool] $RequireCompatibilityHost = $true
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$pluginProject = Join-Path $projectRoot "src/DalamudActCompat"
$outputPath = Join-Path $projectRoot $OutputDirectory

function Get-ChildRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $BasePath,

        [Parameter(Mandatory = $true)]
        [string] $ChildPath
    )

    $baseFullPath = [IO.Path]::GetFullPath($BasePath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $childFullPath = [IO.Path]::GetFullPath($ChildPath)
    if (-not $childFullPath.StartsWith($baseFullPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the release validation directory: $childFullPath"
    }

    return $childFullPath.Substring($baseFullPath.Length)
}

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

$packagedFiles = @(Get-ChildItem -LiteralPath $validationDir -Recurse -File)
$debugSymbols = @($packagedFiles | Where-Object { $_.Extension -ieq ".pdb" })
if ($debugSymbols.Count -gt 0) {
    $relativeDebugSymbols = @($debugSymbols | ForEach-Object {
        Get-ChildRelativePath -BasePath $validationDir -ChildPath $_.FullName
    })
    throw "Plugin ZIP contains debug symbols that can disclose local build paths: $($relativeDebugSymbols -join ', ')"
}

$personalConfigurationNames = @(
    "Advanced Combat Tracker.config.xml",
    "ACT.FoxTTS.config.xml",
    "FFXIV_ACT_Plugin.config.xml",
    "PostNamazu.config.json",
    "Triggernometry.config.xml"
)
$personalConfigurationFiles = @($packagedFiles | Where-Object {
    $_.Name -in $personalConfigurationNames
})
if ($personalConfigurationFiles.Count -gt 0) {
    $relativePersonalConfigurationFiles = @($personalConfigurationFiles | ForEach-Object {
        Get-ChildRelativePath -BasePath $validationDir -ChildPath $_.FullName
    })
    throw "Plugin ZIP contains user-specific configuration files: $($relativePersonalConfigurationFiles -join ', ')"
}

$forbiddenLocalPaths = @(
    [IO.Path]::GetFullPath($projectRoot.Path).TrimEnd([IO.Path]::DirectorySeparatorChar),
    [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile).TrimEnd([IO.Path]::DirectorySeparatorChar)
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

foreach ($packagedFile in $packagedFiles) {
    $contents = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($packagedFile.FullName))
    foreach ($forbiddenLocalPath in $forbiddenLocalPaths) {
        if ($contents.IndexOf($forbiddenLocalPath, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $relativePackagedFile = Get-ChildRelativePath -BasePath $validationDir -ChildPath $packagedFile.FullName
            throw "Plugin ZIP contains local path '$forbiddenLocalPath' in $relativePackagedFile."
        }
    }
}

Write-Host "Validated release privacy: no debug symbols, user configurations, or local build paths."

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

$requiredUiAssets = @(
    "Assets/act-logo.jpg",
    "Assets/act-button.png",
    "Assets/JobIcons/LimitBreak.png"
)
$missingUiAssets = @($requiredUiAssets | Where-Object {
    -not (Test-Path (Join-Path $validationDir $_))
})
if ($missingUiAssets.Count -gt 0) {
    throw "Plugin ZIP is missing required UI assets: $($missingUiAssets -join ', ')"
}

Write-Host "Validated UI assets: $($requiredUiAssets.Count)"

$jobIconRoot = Join-Path $validationDir "Assets/JobIcons"
$jobCodes = @(
    "ACN", "ARC", "AST", "BRD", "BST", "BLM", "BLU", "CNJ",
    "DNC", "DRK", "DRG", "GLA", "GNB", "LNC", "MCH", "MRD",
    "MNK", "NIN", "PLD", "PCT", "PGL", "RPR", "RDM", "ROG",
    "SGE", "SAM", "SCH", "SMN", "THM", "VPR", "WAR", "WHM"
)
foreach ($style in @("Minimal", "Classic", "Flat")) {
    $styleDirectory = Join-Path $jobIconRoot $style
    $iconCount = if (Test-Path $styleDirectory) {
        @(Get-ChildItem $styleDirectory -File -Filter "*.png").Count
    } else {
        0
    }
    if ($iconCount -ne 32) {
        throw "Plugin ZIP contains $iconCount $style job icons instead of 32."
    }
    $missingJobIcons = @($jobCodes | Where-Object {
        -not (Test-Path (Join-Path $styleDirectory "$_.png"))
    })
    if ($missingJobIcons.Count -gt 0) {
        throw "Plugin ZIP is missing $style job icons: $($missingJobIcons -join ', ')"
    }
}

Write-Host "Validated job icons: 96"

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
        "DalamudActCompat.HostAssets.DalamudActCompat.Host.runtimeconfig.json",
        "DalamudActCompat.HostAssets.DalamudActCompat.Protocol.dll",
        "DalamudActCompat.HostAssets.Advanced Combat Tracker.dll",
        "DalamudActCompat.HostAssets.FFXIV_ACT_Plugin.dll",
        "DalamudActCompat.HostAssets.dnlib.dll",
        "DalamudActCompat.HostAssets.Mono.Cecil.dll",
        "DalamudActCompat.HostAssets.System.Speech.dll"
    )
    $missingHostResources = @($requiredHostResources | Where-Object { $_ -notin $hostResources })
    if ($missingHostResources.Count -gt 0) {
        throw "Plugin assembly is missing embedded Compatibility Host resources: $($missingHostResources -join ', ')"
    }

    Write-Host "Validated embedded Compatibility Host resources: $($requiredHostResources.Count)"

    $hostSpeechPath = Join-Path $validationDir "host/System.Speech.dll"
    $runtimeSpeechPath = Join-Path $validationDir "runtimes/win/lib/net10.0/System.Speech.dll"
    if (-not (Test-Path $hostSpeechPath) -or -not (Test-Path $runtimeSpeechPath)) {
        throw "Plugin ZIP is missing the Host or Windows runtime System.Speech implementation."
    }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $hostSpeechPath).Hash -ne
        (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeSpeechPath).Hash) {
        throw "Compatibility Host contains the System.Speech reference assembly instead of the Windows runtime implementation."
    }

    Write-Host "Validated Compatibility Host System.Speech runtime implementation."
}

$cactbotLockRelativePath = "BundledCactbot/bundled-cactbot.lock.json"
$cactbotLockPath = Join-Path $validationDir $cactbotLockRelativePath
if (-not (Test-Path $cactbotLockPath)) {
    throw "Plugin ZIP does not contain $cactbotLockRelativePath."
}

$cactbotLock = Get-Content -LiteralPath $cactbotLockPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($cactbotLock.relativeArchive) -or
    [string]::IsNullOrWhiteSpace($cactbotLock.sha256)) {
    throw "Bundled Cactbot lock file is invalid."
}

$cactbotArchiveRelativePath = "BundledCactbot/$($cactbotLock.relativeArchive)"
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
    "SharpCompress.dll",
    "LICENSE.md",
    "LICENSES/IINACT-GPL-3.0.txt",
    "LICENSES/OverlayPlugin.Core-LICENSE.txt",
    "THIRD_PARTY_NOTICES.md",
    "BundledActPlugins/bundled-plugins.lock.json",
    "BundledActPlugins/triggernometry/Triggernometry.dll",
    "BundledActPlugins/triggernometry/zh-CN.triglations.xml",
    "BundledActPlugins/triggernometry/LICENSE.txt",
    "BundledActPlugins/act.foxtts/ACT.FoxTTS.dll",
    "BundledActPlugins/act.foxtts/LICENSE.txt",
    "BundledActPlugins/postnamazu/PostNamazu.dll",
    $cactbotLockRelativePath,
    $cactbotArchiveRelativePath,
    "BundledCactbot/LICENSE.txt"
)
$missingRuntimeFiles = @($requiredRuntimeFiles | Where-Object {
    -not (Test-Path (Join-Path $validationDir $_))
})
if ($missingRuntimeFiles.Count -gt 0) {
    throw "Plugin ZIP is missing self-hosted ACT runtime files: $($missingRuntimeFiles -join ', ')"
}

$cactbotArchivePath = Join-Path $validationDir $cactbotArchiveRelativePath
$cactbotArchiveSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $cactbotArchivePath).Hash
if ($cactbotArchiveSha256 -ne $cactbotLock.sha256) {
    throw "Bundled Cactbot archive does not match its locked SHA-256."
}

Write-Host "Validated self-hosted ACT runtime files: $($requiredRuntimeFiles.Count)"

Write-Host "Collected plugin ZIP: $destination"
Write-Host "Validated AssemblyVersion: $($manifest.AssemblyVersion)"
Write-Host "Validated DalamudApiLevel: $($manifest.DalamudApiLevel)"
