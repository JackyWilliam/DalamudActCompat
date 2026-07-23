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
https://github.com/JackyWilliam/DalamudActCompat/releases/download/v0.1.4/DalamudActCompat.zip
```

The raw URL will only work after `JackyWilliam/DalamudActCompatRepo` exists on GitHub and contains `pluginmaster.json` at the repository root. The install link will only work after the source repository has a matching release with `DalamudActCompat.zip`.

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
dotnet build DalamudActCompat.slnx -c Release
```

After the build succeeds, create a release tag and upload the ZIP from Windows:

```powershell
gh release create v0.1.4 ".\artifacts\release\DalamudActCompat.zip" --repo JackyWilliam/DalamudActCompat --title v0.1.4 --notes "Fix compatibility host lookup after custom repository installation."
```

GitHub-hosted runners do not have local XIVLauncher/Dalamud dev files, so the default CI does not produce this ZIP. Use a Windows machine with Dalamud installed, or configure a Windows self-hosted runner and run `.github/workflows/release.yml`.

## Update PluginMaster

After the release ZIP exists, update and sync the custom repository:

```powershell
./tools/update-pluginmaster.ps1 -Version 0.1.4 -Changelog "Fix compatibility host lookup after custom repository installation."
Copy-Item repo/pluginmaster.json ..\DalamudActCompatRepo\pluginmaster.json -Force
```

Then from `DalamudActCompatRepo`:

```powershell
git add pluginmaster.json
git commit -m "chore: 更新 Dalamud 插件仓库 0.1.4"
git push
```

Before uploading a release, always collect and validate the zip:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\collect-release.ps1 -ExpectedAssemblyVersion 0.1.4.0 -ExpectedDalamudApiLevel 15
```

If the script reports `AssemblyVersion`, `DalamudApiLevel`, or `host/DalamudActCompat.Host.exe` mismatch, run a clean rebuild before publishing:

```powershell
dotnet clean DalamudActCompat.slnx -c Release
dotnet build DalamudActCompat.slnx -c Release -p:DalamudLibPath="C:\Users\jacky\AppData\Roaming\XIVLauncherCN\addon\Hooks\Dev\"
```

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

The plugin is still a foundation build. If it loads, it should show `/actcompat` windows and parser status. It should not yet parse combat because IINACT/NotACT/FFXIV_ACT_Plugin runtime integration is not implemented.

Use `/actcompat host` to start the out-of-process Compatibility Host sample bridge. The Meter should begin updating once per second with IPC sample combatants. Use `/actcompat stop` to stop it.
