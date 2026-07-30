param(
    [Parameter(Mandatory = $false)]
    [switch] $Check
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$iinactProjectPath = Join-Path $projectRoot "vendor/IINACT/IINACT/IINACT.csproj"
$dependencyDirectory = Join-Path $projectRoot "vendor/IINACT/external_dependencies"
$dalamudCandidates = @(
    (Join-Path $env:APPDATA "XIVLauncherCN/addon/Hooks/dev"),
    (Join-Path $env:APPDATA "XIVLauncher/addon/Hooks/dev")
)
$dalamudDirectory = $dalamudCandidates |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ "Mono.Cecil.dll") } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($dalamudDirectory)) {
    throw "Could not find Dalamud development assemblies containing Mono.Cecil.dll."
}
$dalamudDirectory = $dalamudDirectory.TrimEnd("\", "/") + [IO.Path]::DirectorySeparatorChar
[xml] $iinactProject = Get-Content -LiteralPath $iinactProjectPath -Raw
$iinactVersion = [string] $iinactProject.Project.PropertyGroup.Version |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($iinactVersion)) {
    throw "Could not read the IINACT version from $iinactProjectPath."
}

$arguments = @(
    "run",
    "--project",
    (Join-Path $projectRoot "tools/ParserDependencySync/ParserDependencySync.csproj"),
    "-p:DalamudLibPath=$dalamudDirectory",
    "--",
    $dependencyDirectory,
    $iinactVersion
)
if ($Check) {
    $arguments += "--check"
}

& dotnet $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Parser dependency synchronization failed with exit code $LASTEXITCODE."
}
