param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$targetFramework = 'net10.0-windows10.0.17763.0'
$testProject = Join-Path $repoRoot 'tests\DalamudActCompat.HostSmokeTests\DalamudActCompat.HostSmokeTests.csproj'
$testOutput = Join-Path $repoRoot "tests\DalamudActCompat.HostSmokeTests\bin\$configuration\$targetFramework"
$testExecutable = Join-Path $testOutput 'DalamudActCompat.HostSmokeTests.exe'
$hostExecutable = Join-Path $testOutput 'DalamudActCompat.Host.exe'
$pluginRoot = Join-Path $repoRoot 'vendor\BundledActPlugins'
$probeConfigRoot = Join-Path ([IO.Path]::GetTempPath()) ("dact-mapeffect-" + [Guid]::NewGuid().ToString('N'))

try {
    if (-not $NoBuild) {
        dotnet build $testProject --configuration $configuration
        if ($LASTEXITCODE -ne 0) {
            throw "Host smoke build failed with exit code $LASTEXITCODE."
        }
    }

    if (-not (Test-Path -LiteralPath $testExecutable) -or -not (Test-Path -LiteralPath $hostExecutable)) {
        throw 'Build outputs are missing. Run without -NoBuild first.'
    }

    New-Item -ItemType Directory -Path $probeConfigRoot | Out-Null
    # Keep this probe on the real plugin pair: the issue is the interaction between ACT legacy
    # line formatting and Triggernometry's production regular-expression engine.
    & $testExecutable `
        '--probe-triggernometry-mapeffect' `
        $hostExecutable `
        $pluginRoot `
        $probeConfigRoot
    if ($LASTEXITCODE -ne 0) {
        throw "MapEffect probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeConfigRoot) {
        $resolvedProbeRoot = (Resolve-Path -LiteralPath $probeConfigRoot).Path
        $resolvedTempRoot = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path.TrimEnd('\')
        if ($resolvedProbeRoot.StartsWith($resolvedTempRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedProbeRoot -Recurse -Force
        }
    }
}
