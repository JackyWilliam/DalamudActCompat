# Rebuilds the bundled PostNamazu from upstream source at a fixed commit, with the same steps as
# upstream's own CI (.github/workflows/dotnet-desktop.yml): restore, unpack upstream's reference
# library archive into Lib, build Release.
#
# Why DACT builds it at all: Simulant v0.1.0.0 makes every native call through PostNamazu's call
# scheduler (PostNamazu.Call / DirectCall / ExecuteInFrameLock, field CallScheduler). Upstream
# merged that on master in May 2026 but the latest official release is still 1.3.6.6 without
# it. The snapshot has the surface of the 1.3.6.7 builds users already run, and keeps the
# version number the source declares (1.3.6.6); nothing here invents a release.
#
# The output is byte-for-byte reproducible on one machine and path. It embeds debug symbols of
# two helper projects, which contain source paths, so another checkout location can produce a
# different hash for the same code. tools/sync-bundled-act-plugins.ps1 pins the hash of the
# build that was reviewed and committed.
param(
    [Parameter(Mandatory = $false)]
    [string] $WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) "dact-postnamazu-snapshot"),

    [Parameter(Mandatory = $false)]
    [string] $Commit = "521007c86343782c6bc9a52d5e192e520fcf32ca",

    [Parameter(Mandatory = $false)]
    [string] $LibSha256 = "d1f58002e30ec0df1a1cd9fed3fe97a06357f9ad04ae0eebcb6ee6e3d5d7e84f",

    # Copies the result over vendor/BundledActPlugins/postnamazu/PostNamazu.dll.
    [switch] $Install
)

$ErrorActionPreference = "Stop"
$repository = "https://github.com/Natsukage/PostNamazu.git"
$libUrl = "https://raw.githubusercontent.com/Natsukage/Assets/main/PostNamazu/Lib.zip"

if (-not (Test-Path -LiteralPath (Join-Path $WorkDirectory ".git"))) {
    git clone --quiet $repository $WorkDirectory
    if ($LASTEXITCODE -ne 0) { throw "Could not clone $repository." }
}
git -C $WorkDirectory fetch --quiet origin
git -C $WorkDirectory checkout --quiet --detach $Commit
if ($LASTEXITCODE -ne 0) { throw "Could not check out $Commit." }
if ((git -C $WorkDirectory status --porcelain --untracked-files=no) -ne $null) {
    throw "The PostNamazu checkout has local changes; the snapshot must be built from the commit as it is."
}

$libArchive = Join-Path $WorkDirectory "Lib.zip"
Invoke-WebRequest -Uri $libUrl -OutFile $libArchive
$actualLib = (Get-FileHash -LiteralPath $libArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualLib -ne $LibSha256) {
    # The archive is not versioned upstream. A change means the build inputs changed and the
    # result has to be reviewed again before it replaces the pinned snapshot.
    throw "Upstream reference archive changed: expected $LibSha256, got $actualLib."
}
$lib = Join-Path $WorkDirectory "Lib"
if (Test-Path -LiteralPath $lib) { Remove-Item -LiteralPath $lib -Recurse -Force }
Expand-Archive -LiteralPath $libArchive -DestinationPath $lib

Get-ChildItem -LiteralPath $WorkDirectory -Recurse -Directory |
    Where-Object { $_.Name -in "bin", "obj" } |
    Remove-Item -Recurse -Force
dotnet build (Join-Path $WorkDirectory "PostNamazu.sln") --configuration Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "PostNamazu build failed." }

$output = Join-Path $WorkDirectory "PostNamazu/bin/Release/netstandard2.0/PostNamazu.dll"
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "PostNamazu snapshot $Commit"
Write-Host "  SDK     $(dotnet --version)"
Write-Host "  size    $((Get-Item -LiteralPath $output).Length) bytes"
Write-Host "  sha256  $hash"
if ($Install) {
    $destination = Join-Path $PSScriptRoot "../vendor/BundledActPlugins/postnamazu/PostNamazu.dll"
    Copy-Item -LiteralPath $output -Destination $destination -Force
    Write-Host "Installed to $destination. Update the pinned hash in tools/sync-bundled-act-plugins.ps1 and the lock file if it changed."
}
