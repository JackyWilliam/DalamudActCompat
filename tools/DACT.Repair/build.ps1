param(
    [string] $CorePackage,
    [string] $DalamudLibPath,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts/repair-plugin'),
    [string] $PreviewPath = '-'
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (-not $CorePackage) {
    $CorePackage = Join-Path $output 'DalamudActCompat-core.zip'
    if (-not (Test-Path -LiteralPath $CorePackage)) {
        Invoke-WebRequest -Uri 'https://github.com/JackyWilliam/DalamudActCompat/releases/download/v0.4.4.0/DalamudActCompat-core.zip' -OutFile $CorePackage
    }
}
$CorePackage = [IO.Path]::GetFullPath($CorePackage)
if ((Get-FileHash -LiteralPath $CorePackage).Hash.ToLowerInvariant() -ne '17a6b205dad06e616c567cee343d29579c4980016c042719924b9a363ac6ace1') { throw 'Pinned repair payload hash mismatch.' }
if (-not $DalamudLibPath) { $DalamudLibPath = if ($env:DALAMUD_HOME) { $env:DALAMUD_HOME } else { Join-Path $env:APPDATA 'XIVLauncherCN/addon/Hooks/dev' } }
$DalamudLibPath = [IO.Path]::GetFullPath($DalamudLibPath).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
if (-not (Test-Path -LiteralPath (Join-Path $DalamudLibPath 'Dalamud.dll'))) { throw 'Dalamud reference assemblies missing.' }
$project = Join-Path $PSScriptRoot '../../tests/DalamudActCompatRepair.SmokeTests/DalamudActCompatRepair.SmokeTests.csproj'
& dotnet build $project -c Release "-p:DalamudLibPath=$DalamudLibPath" "-p:RepairCorePackage=$CorePackage" -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Repair plugin build failed.' }

# The only EXE built here is an isolated test fixture, never a distributed tool.
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$fixtureSource = Join-Path $output 'WrongVersionFixture.cs'
'using System.Reflection; [assembly: AssemblyVersion("4.4.0.0")] public class SyntheticWrongVersion { }' | Set-Content -LiteralPath $fixtureSource -Encoding utf8
$fixture = Join-Path $output 'WrongVersionFixture.dll'
& $compiler /nologo /target:library /debug- "/out:$fixture" $fixtureSource
if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
$junctionTarget = Join-Path $output ('junction-target-' + [Guid]::NewGuid().ToString('N'))
$junction = Join-Path $output ('junction-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $junctionTarget | Out-Null
New-Item -ItemType Junction -Path $junction -Target $junctionTarget | Out-Null
$testExe = Join-Path $PSScriptRoot '../../tests/DalamudActCompatRepair.SmokeTests/bin/Release/net10.0-windows10.0.17763.0/DalamudActCompatRepair.SmokeTests.exe'
& $testExe $fixture $PreviewPath $junction
if ($LASTEXITCODE -ne 0) { throw 'Repair plugin regression failed.' }

$binary = Join-Path $PSScriptRoot '../../src/DalamudActCompatRepair/bin/Release'
$archive = Join-Path $output 'DalamudActCompatRepair-0.1.0.0.zip'
$stage = Join-Path $output ('package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($name in @('DalamudActCompatRepair.dll','DalamudActCompatRepair.json')) { Copy-Item -LiteralPath (Join-Path $binary $name) -Destination (Join-Path $stage $name) }
$manifest = Get-Content -LiteralPath (Join-Path $stage 'DalamudActCompatRepair.json') -Raw | ConvertFrom-Json
if ($manifest.InternalName -ne 'DalamudActCompatRepair' -or $manifest.AssemblyVersion -ne '0.1.0.0' -or $manifest.DalamudApiLevel -ne 15) { throw 'Repair manifest mismatch.' }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
$checksum = (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($archive)
$checksum | Set-Content -LiteralPath (Join-Path $output 'DalamudActCompatRepair-0.1.0.0.sha256.txt') -Encoding ascii
Write-Output $checksum
