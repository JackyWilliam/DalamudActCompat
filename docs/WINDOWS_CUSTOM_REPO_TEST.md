# Windows Custom Repository Test

This is the shortest path to test the plugin from a custom Dalamud repository on Windows.

## URLs

Source repository:

```text
https://github.com/raynording/DalamudActCompat
```

Custom repository raw URL:

```text
https://raw.githubusercontent.com/raynording/DalamudActCompatRepo/main/pluginmaster.json
```

Release ZIP expected by `pluginmaster.json`:

```text
https://github.com/raynording/DalamudActCompat/releases/download/v0.1.0/DalamudActCompat.zip
```

The raw URL will only work after `raynording/DalamudActCompatRepo` exists on GitHub and contains `pluginmaster.json` at the repository root. The install link will only work after the source repository has a `v0.1.0` release with `DalamudActCompat.zip`.

## Prepare GitHub Repositories

From Windows PowerShell after authenticating GitHub CLI:

```powershell
gh auth login -h github.com
gh repo create raynording/DalamudActCompat --public --source . --remote origin --push
```

For the custom repository, run from the `DalamudActCompatRepo` folder:

```powershell
gh repo create raynording/DalamudActCompatRepo --public --source . --remote origin --push
```

## Build and Release

From the source repository:

```powershell
dotnet restore DalamudActCompat.slnx
dotnet build DalamudActCompat.slnx -c Release
```

After the build succeeds, create a release ZIP through the included workflow by pushing a tag:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The release workflow should upload `DalamudActCompat.zip`. If CI fails because the ZIP collection path differs, inspect the workflow artifact output and update `tools/collect-release.ps1`.

## Update PluginMaster

After the release ZIP exists, update and sync the custom repository:

```powershell
./tools/update-pluginmaster.ps1 -Version 0.1.0 -Changelog "Initial Windows test build."
Copy-Item repo/pluginmaster.json ..\DalamudActCompatRepo\pluginmaster.json -Force
```

Then from `DalamudActCompatRepo`:

```powershell
git add pluginmaster.json
git commit -m "chore: 更新 Dalamud 插件仓库 0.1.0"
git push
```

## Add to Dalamud

In-game:

1. Open `/xlsettings`.
2. Go to Experimental.
3. Add the raw custom repository URL:

```text
https://raw.githubusercontent.com/raynording/DalamudActCompatRepo/main/pluginmaster.json
```

4. Save, open `/xlplugins`, and search for `Dalamud ACT Compat`.

## Current Expected Behavior

The plugin is still a foundation build. If it loads, it should show `/actcompat` windows and parser status. It should not yet parse combat because IINACT/NotACT/FFXIV_ACT_Plugin runtime integration is not implemented.
