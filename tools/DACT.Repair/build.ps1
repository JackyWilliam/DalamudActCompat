param(
    [string] $CorePackage,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts/repair'),
    [string] $PreviewPath
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$expected = '17a6b205dad06e616c567cee343d29579c4980016c042719924b9a363ac6ace1'
if (-not $CorePackage) {
    $CorePackage = Join-Path $output 'DalamudActCompat-core.zip'
    if (-not (Test-Path -LiteralPath $CorePackage)) {
        Invoke-WebRequest -Uri 'https://github.com/JackyWilliam/DalamudActCompat/releases/download/v0.4.4.0/DalamudActCompat-core.zip' -OutFile $CorePackage
    }
}
$CorePackage = [IO.Path]::GetFullPath($CorePackage)
if ((Get-FileHash -LiteralPath $CorePackage -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Pinned 0.4.4.0 package SHA256 mismatch.' }

# Windows ships this compiler/runtime; the repair executable requires no separate
# .NET 10 install, package restore, game process or network connection at runtime.
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.IO.Compression.dll') | ForEach-Object { '/reference:' + (Join-Path (Split-Path $compiler) $_) }
$common = @('/nologo','/optimize+','/debug-','/codepage:65001','/utf8output','/platform:anycpu') + $references
$resource = '/resource:' + $CorePackage + ',DACT.Core.zip'
$engine = Join-Path $PSScriptRoot 'RepairEngine.cs'
$form = Join-Path $PSScriptRoot 'RepairForm.cs'
$exe = Join-Path $output 'DACT-Repair-1.0.0.exe'
& $compiler @common /target:winexe "/out:$exe" "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" $resource $engine $form
if ($LASTEXITCODE -ne 0) { throw 'Repair tool build failed.' }

$fixtureSource = Join-Path $output 'WrongVersionFixture.cs'
'using System.Reflection; [assembly: AssemblyVersion("4.4.0.0")] public class SyntheticWrongVersion { }' | Set-Content -LiteralPath $fixtureSource -Encoding utf8
$fixture = Join-Path $output 'WrongVersionFixture.dll'
& $compiler /nologo /target:library /debug- "/out:$fixture" $fixtureSource
if ($LASTEXITCODE -ne 0) { throw 'Test fixture build failed.' }
$tests = Join-Path $output 'DACT.Repair.Tests.exe'
& $compiler @common /target:exe /main:DactRepair.Tests "/out:$tests" $resource $engine $form (Join-Path $PSScriptRoot 'RepairTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Repair tests build failed.' }
$junctionTarget = Join-Path $output ('junction-target-' + [Guid]::NewGuid().ToString('N'))
$junction = Join-Path $output ('junction-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $junctionTarget | Out-Null
New-Item -ItemType Junction -Path $junction -Target $junctionTarget | Out-Null
if (-not $PreviewPath) { $PreviewPath = '-' }
& $tests $fixture $PreviewPath $junction
if ($LASTEXITCODE -ne 0) { throw 'Repair regression failed.' }
$checksum = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($exe)
$checksum | Set-Content -LiteralPath (Join-Path $output 'DACT-Repair-1.0.0.sha256.txt') -Encoding ascii
Write-Output $checksum
