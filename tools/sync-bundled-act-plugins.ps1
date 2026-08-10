param(
    [Parameter(Mandatory = $false)]
    [switch] $Check
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$destinationRoot = Join-Path $projectRoot "vendor/BundledActPlugins"
$workRoot = Join-Path $projectRoot "artifacts/plugin-sync"
$headers = @{
    "User-Agent" = "DalamudActCompat bundled plugin sync"
    "Accept" = "application/vnd.github+json"
}
if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    $headers["Authorization"] = "Bearer $($env:GH_TOKEN)"
}

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

function Get-LatestReleaseAsset {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [string] $AssetPattern
    )

    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$Repository/releases/latest" `
        -Headers $headers
    $asset = @($release.assets | Where-Object { $_.name -match $AssetPattern })
    if ($asset.Count -ne 1) {
        throw "Expected exactly one $Repository release asset matching $AssetPattern, found $($asset.Count)."
    }

    return @{
        Tag = [string] $release.tag_name
        ReleaseUrl = [string] $release.html_url
        Name = [string] $asset[0].name
        DownloadUrl = [string] $asset[0].browser_download_url
    }
}

function Get-AssemblyVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Assembly has no file version: $Path"
    }

    return $version
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ZipEntrySha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $EntryPath
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object {
            $_.FullName.Replace("\", "/") -ieq $EntryPath.Replace("\", "/")
        })
        if ($entries.Count -ne 1) {
            throw "Expected exactly one ZIP entry '$EntryPath' in $Path, found $($entries.Count)."
        }

        $stream = $entries[0].Open()
        try {
            return (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.ToLowerInvariant()
        } finally {
            $stream.Dispose()
        }
    } finally {
        $archive.Dispose()
    }
}

function Remove-TrailingWhitespace {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $content = [IO.File]::ReadAllText($Path, $utf8)
    $content = [Text.RegularExpressions.Regex]::Replace(
        $content,
        "[ `t]+(?=`r?$)",
        "",
        [Text.RegularExpressions.RegexOptions]::Multiline)
    [IO.File]::WriteAllText($Path, $content, $utf8)
}

function Copy-Or-Verify {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $Destination,

        [Parameter(Mandatory = $false)]
        [switch] $NormalizeText
    )

    if ($Check) {
        if (-not (Test-Path -LiteralPath $Destination)) {
            throw "Bundled plugin file is missing: $Destination"
        }

        $matches = if ($NormalizeText) {
            $sourceText = [IO.File]::ReadAllText($Source).
                Replace("`r`n", "`n").
                Replace("`r", "`n")
            $destinationText = [IO.File]::ReadAllText($Destination).
                Replace("`r`n", "`n").
                Replace("`r", "`n")
            $sourceText -ceq $destinationText
        } else {
            (Get-Sha256 $Source) -eq (Get-Sha256 $Destination)
        }

        if (-not $matches) {
            throw "Bundled plugin file is not current: $Destination"
        }

        return
    }

    New-Item -ItemType Directory -Force -Path (Split-Path $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

$triggerDirectory = Join-Path $workRoot "triggernometry"
$foxDirectory = Join-Path $workRoot "act.foxtts"
$postNamazuDirectory = Join-Path $workRoot "postnamazu"
New-Item -ItemType Directory -Force -Path `
    $triggerDirectory, `
    $foxDirectory, `
    $postNamazuDirectory | Out-Null

$silverPackage = Join-Path $destinationRoot "silverdasher/SilverDasher-0.6.0.4-cafe.zip"
$silverPackageSha256 = "8d73b14af27cc4781ddf09b7926c5d99a11cd5b8a02b94fd90430acf38371866"
$silverAssemblyPath = "Plugins/SilverDasher/SilverDasher.dll"
$silverAssemblySha256 = "a3f356743a438b49cc0796858ca3b127e79de90e4d7e960177ebda50a8568cf8"
if (-not (Test-Path -LiteralPath $silverPackage) -or
    (Get-Sha256 $silverPackage) -ne $silverPackageSha256 -or
    (Get-ZipEntrySha256 $silverPackage $silverAssemblyPath) -ne $silverAssemblySha256) {
    throw "The bundled SilverDasher 0.6.0.4 complete package no longer matches its fixed hashes."
}

$triggerDllUrl = "https://1824544011.v.123pan.cn/1824544011/Triggernometry_Release_CN/Triggernometry.dll"
$triggerTranslationUrl = "https://1824544011.v.123pan.cn/1824544011/Triggernometry_Release_CN/zh-CN.triglations.xml"
$triggerDll = Join-Path $triggerDirectory "Triggernometry.dll"
$triggerTranslation = Join-Path $triggerDirectory "zh-CN.triglations.xml"
$triggerLicense = Join-Path $triggerDirectory "LICENSE.txt"
Invoke-WebRequest -Uri $triggerDllUrl -OutFile $triggerDll
Invoke-WebRequest -Uri $triggerTranslationUrl -OutFile $triggerTranslation
Remove-TrailingWhitespace $triggerTranslation
Invoke-WebRequest `
    -Uri "https://raw.githubusercontent.com/MnFeN/Triggernometry/new/LICENSE" `
    -OutFile $triggerLicense

$foxRelease = Get-LatestReleaseAsset "Noisyfox/ACT.FoxTTS" "^ACT\.FoxTTS-.+-Release\.7z$"
$foxArchive = Join-Path $foxDirectory $foxRelease.Name
$foxExpanded = Join-Path $foxDirectory "expanded"
$foxLicense = Join-Path $foxDirectory "LICENSE.txt"
New-Item -ItemType Directory -Force -Path $foxExpanded | Out-Null
Invoke-WebRequest -Uri $foxRelease.DownloadUrl -OutFile $foxArchive
tar.exe -xf $foxArchive -C $foxExpanded
if ($LASTEXITCODE -ne 0) {
    throw "Could not extract $($foxRelease.Name) with tar.exe."
}
$foxDll = Join-Path $foxExpanded "ACT.FoxTTS.dll"
Invoke-WebRequest `
    -Uri "https://raw.githubusercontent.com/Noisyfox/ACT.FoxTTS/master/LICENSE" `
    -OutFile $foxLicense

$postNamazuRelease = Get-LatestReleaseAsset "Natsukage/PostNamazu" "^PostNamazu\.zip$"
$postNamazuArchive = Join-Path $postNamazuDirectory $postNamazuRelease.Name
$postNamazuExpanded = Join-Path $postNamazuDirectory "expanded"
New-Item -ItemType Directory -Force -Path $postNamazuExpanded | Out-Null
Invoke-WebRequest -Uri $postNamazuRelease.DownloadUrl -OutFile $postNamazuArchive
Expand-Archive -LiteralPath $postNamazuArchive -DestinationPath $postNamazuExpanded -Force
$postNamazuDll = Get-ChildItem `
    -LiteralPath $postNamazuExpanded `
    -Recurse `
    -Filter "PostNamazu.dll" |
    Select-Object -First 1
if ($null -eq $postNamazuDll) {
    throw "PostNamazu release does not contain PostNamazu.dll."
}

$plugins = @(
    [ordered]@{
        id = "triggernometry"
        name = "Triggernometry CN Maintained Edition"
        version = Get-AssemblyVersion $triggerDll
        author = "Paissa Heavy Industries"
        maintainer = "MnFeN (Chinese maintained edition)"
        copyright = "(c) 2016-2025 Paissa Heavy Industries"
        projectUrl = "https://github.com/MnFeN/Triggernometry"
        downloadUrl = $triggerDllUrl
        sourceUrl = "https://github.com/MnFeN/Triggernometry"
        license = "MIT"
        licenseFile = "triggernometry/LICENSE.txt"
        relativeAssembly = "triggernometry/Triggernometry.dll"
        sha256 = Get-Sha256 $triggerDll
        enableAfterInstall = $true
    },
    [ordered]@{
        id = "act.foxtts"
        name = "ACT.FoxTTS"
        version = Get-AssemblyVersion $foxDll
        author = "Noisyfox"
        maintainer = "Noisyfox"
        copyright = "Copyright (c) 2018-2022 Noisyfox"
        projectUrl = "https://github.com/Noisyfox/ACT.FoxTTS"
        downloadUrl = $foxRelease.DownloadUrl
        sourceUrl = "https://github.com/Noisyfox/ACT.FoxTTS"
        license = "GPL-3.0"
        licenseFile = "act.foxtts/LICENSE.txt"
        relativeAssembly = "act.foxtts/ACT.FoxTTS.dll"
        sha256 = Get-Sha256 $foxDll
        enableAfterInstall = $true
    },
    [ordered]@{
        id = "postnamazu"
        name = "PostNamazu"
        version = Get-AssemblyVersion $postNamazuDll.FullName
        author = "Natsukage"
        maintainer = "Natsukage"
        copyright = ""
        projectUrl = "https://github.com/Natsukage/PostNamazu"
        downloadUrl = $postNamazuRelease.DownloadUrl
        sourceUrl = "https://github.com/Natsukage/PostNamazu"
        license = "Redistribution authorized by the author; the upstream repository declares no SPDX license"
        licenseFile = ""
        relativeAssembly = "postnamazu/PostNamazu.dll"
        sha256 = Get-Sha256 $postNamazuDll.FullName
        enableAfterInstall = $true
    },
    [ordered]@{
        id = "silverdasher"
        name = "银山雀儿 / SilverDasher"
        version = "0.6.0.4"
        author = "The Players / SilverDasher"
        maintainer = "SilverDasher maintainers / QQ group 582145824"
        copyright = "Copyright © The Players 2022-2025"
        projectUrl = "https://www.ffcafe.cn/act/"
        downloadUrl = "https://www.ffcafe.cn/act/"
        sourceUrl = "https://www.ffcafe.cn/act/"
        license = "The supplied package contains no license file; redistribution of this bundled version was authorized by the upstream maintainer on 2026-08-10"
        licenseFile = ""
        relativeAssembly = $silverAssemblyPath
        sha256 = $silverAssemblySha256
        relativePackage = "silverdasher/SilverDasher-0.6.0.4-cafe.zip"
        packageSha256 = $silverPackageSha256
        disableOnlineUpdates = $true
        enableAfterInstall = $false
    }
)

Copy-Or-Verify `
    $triggerDll `
    (Join-Path $destinationRoot "triggernometry/Triggernometry.dll")
Copy-Or-Verify `
    $triggerTranslation `
    (Join-Path $destinationRoot "triggernometry/zh-CN.triglations.xml") `
    -NormalizeText
Copy-Or-Verify `
    $triggerLicense `
    (Join-Path $destinationRoot "triggernometry/LICENSE.txt") `
    -NormalizeText
Copy-Or-Verify `
    $foxDll `
    (Join-Path $destinationRoot "act.foxtts/ACT.FoxTTS.dll")
Copy-Or-Verify `
    $foxLicense `
    (Join-Path $destinationRoot "act.foxtts/LICENSE.txt") `
    -NormalizeText
Copy-Or-Verify `
    $postNamazuDll.FullName `
    (Join-Path $destinationRoot "postnamazu/PostNamazu.dll")

$lockPath = Join-Path $destinationRoot "bundled-plugins.lock.json"
$lock = [ordered]@{
    schemaVersion = 1
    plugins = $plugins
}
$expectedLock = $lock | ConvertTo-Json -Depth 5
if ($Check) {
    if (-not (Test-Path -LiteralPath $lockPath)) {
        throw "Bundled plugin lock file is missing: $lockPath"
    }

    $actualLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    $actualById = @{}
    foreach ($plugin in $actualLock.plugins) {
        $actualById[$plugin.id] = $plugin
    }

    foreach ($plugin in $plugins) {
        $actual = $actualById[$plugin.id]
        $expectedRelativePackage = if ($plugin.Contains("relativePackage")) {
            [string] $plugin.relativePackage
        } else {
            ""
        }
        $expectedPackageSha256 = if ($plugin.Contains("packageSha256")) {
            [string] $plugin.packageSha256
        } else {
            ""
        }
        $expectedDisableOnlineUpdates = if ($plugin.Contains("disableOnlineUpdates")) {
            [bool] $plugin.disableOnlineUpdates
        } else {
            $false
        }
        $expectedEnableAfterInstall = if ($plugin.Contains("enableAfterInstall")) {
            [bool] $plugin.enableAfterInstall
        } else {
            $true
        }
        if ($null -eq $actual -or
            $actual.version -ne $plugin.version -or
            $actual.downloadUrl -ne $plugin.downloadUrl -or
            $actual.relativeAssembly -ne $plugin.relativeAssembly -or
            $actual.sha256 -ne $plugin.sha256 -or
            [string] $actual.relativePackage -ne $expectedRelativePackage -or
            [string] $actual.packageSha256 -ne $expectedPackageSha256 -or
            [bool] $actual.disableOnlineUpdates -ne $expectedDisableOnlineUpdates -or
            [bool] $actual.enableAfterInstall -ne $expectedEnableAfterInstall) {
            throw "Bundled plugin lock is not current for $($plugin.id)."
        }
    }

    if ($actualById.Count -ne $plugins.Count) {
        throw "Bundled plugin lock contains an unexpected or missing plugin entry."
    }

    Write-Host "Bundled ACT plugins are current."
    return
}

New-Item -ItemType Directory -Force -Path $destinationRoot | Out-Null
$expectedLock | Set-Content -LiteralPath $lockPath -Encoding utf8
Write-Host "Synchronized bundled ACT plugins:"
foreach ($plugin in $plugins) {
    Write-Host "  $($plugin.name) $($plugin.version) [$($plugin.sha256)]"
}
