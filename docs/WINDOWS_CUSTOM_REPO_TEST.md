# Windows Custom Repository Test

This is the shortest path to test the plugin from a custom Dalamud repository on Windows.

## URLs

Source repository:

```text
https://github.com/JackyWilliam/DalamudActCompat
```

Custom repository raw URL:

```text
https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json
```

Release ZIP expected by `pluginmaster.json`:

```text
https://github.com/JackyWilliam/DalamudActCompat/releases/download/v0.4.0.4/DalamudActCompat-core.zip
```

The raw URL will only work after `JackyWilliam/DalamudActCompatRepo` exists on GitHub and contains `pluginmaster.json` at the repository root. The install link will only work after the source repository has a matching release with the core ZIP, resource manifest, and all three referenced resource packs.

## Prepare GitHub Repositories

From Windows PowerShell after authenticating GitHub CLI:

```powershell
gh auth login -h github.com
gh repo create JackyWilliam/DalamudActCompat --public --source . --remote origin --push
```

For the custom repository, run from the `DalamudActCompatRepo` folder:

```powershell
gh repo create JackyWilliam/DalamudActCompatRepo --public --source . --remote origin --push
```

## Build and Release

From the source repository:

```powershell
dotnet restore DalamudActCompat.slnx
dotnet build src/DalamudActCompat/DalamudActCompat.csproj -c Release --no-restore -p:DebugType=none -p:DebugSymbols=false
```

Build the plugin project directly for release packaging. A solution build can select Debug outputs for vendor project references absent from the solution, even with `-c Release`. Debug symbols and embedded local PDB paths fail the release privacy check; do not disable that check. Restore and build each smoke test project separately before running it when using this path.

Run the scanner resource-lifetime regression in its isolated process before packaging:

```powershell
& .\tests\DalamudActCompat.ScannerSmokeTests\bin\Release\net10.0-windows\DalamudActCompat.ScannerSmokeTests.exe
```

This checks that unrelated event handles survive repeated scanner initialization and garbage collection, that scanner results still refer to the live module, and that failed image opens/mappings release their resources. Build CI and the Release workflow also run this check. When updating from a version with the old handle-lifetime bug, fully restart the game; plugin reload alone cannot repair already-invalid process handles.

After the build succeeds, run the Release workflow. It validates the package, uploads all resource packs to a draft first, uploads the core last, and only then publishes the release:

```powershell
gh workflow run Release -f tag=v0.4.0.4 --repo JackyWilliam/DalamudActCompat --ref main
```

Hosted Build CI compiles against public SDK 15 references and runs the Package, Host, Legacy Resource, and scanner suites. Formal packaging uses the local launcher references. If no Windows self-hosted runner is available, build the exact reviewed and merged main commit locally after CI passes, verify the dependency locks with the three `sync-*.ps1 -Check` commands in `release.yml`, and run the collector below. Create a draft release targeting that commit, upload the three resource packs first, then the manifest and core ZIP. Verify all five assets before publishing; download them anonymously afterward and compare SHA-256 hashes with the validated local files.

## Update PluginMaster

Update source metadata as part of the release changes:

```powershell
./tools/update-pluginmaster.ps1 -Version 0.4.0.4 -Changelog "Describe the final user-visible changes."
```

After the matching public release and all its assets exist, run the distribution sync workflow (also scheduled automatically):

```powershell
gh workflow run sync-latest-release.yml --repo JackyWilliam/DalamudActCompatRepo
```

Verify the public raw index has the new version, API level, and working core download links. The sync script derives its changelog from bullets in the first level-two section of the release notes.

Before uploading a release, always collect and validate the zip:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\collect-release.ps1 -ExpectedAssemblyVersion 0.4.0.4 -ExpectedDalamudApiLevel 15
```

If the script reports `AssemblyVersion`, `DalamudApiLevel`, or `host/DalamudActCompat.Host.exe` mismatch, run a clean rebuild before publishing:

```powershell
$dalamud = (Join-Path $env:APPDATA 'XIVLauncherCN/addon/Hooks/dev').TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
dotnet clean src/DalamudActCompat/DalamudActCompat.csproj -c Release
dotnet build src/DalamudActCompat/DalamudActCompat.csproj -c Release -p:DebugType=none -p:DebugSymbols=false "-p:DalamudLibPath=$dalamud"
```

Use `XIVLauncher` instead of `XIVLauncherCN` for the international launcher. The collector replaces generated packages and validation files in its output directory, so dedicate that directory to release artifacts.

## Add to Dalamud

In-game:

1. Open `/xlsettings`.
2. Go to Experimental.
3. Add the raw custom repository URL:

```text
https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json
```

4. Save, open `/xlplugins`, and search for `Dalamud ACT Compat`.

## Current Expected Behavior

Open `/actcompat` to check parser and extension status. The core package downloads its matching resource packs as needed; subsequent starts can reuse verified cached resources. Fully restart the game after updating to `0.4.0.4` so the parser handle fix takes effect.
