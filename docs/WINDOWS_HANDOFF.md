# Windows Handoff

This project should be built and tested on Windows for the next stage. The macOS workspace has source code, repository metadata, and CI scaffolding, but it cannot fully validate the Dalamud plugin without local Dalamud development files.

## Why Windows

- `FFXIV_ACT_Plugin` and most ACT plugin compatibility work are Windows-first.
- The compatibility host needs WinForms-compatible ACT plugin surfaces.
- XIVLauncher/Dalamud development paths are easier to satisfy on Windows.
- Future real-game verification needs FFXIV, Dalamud, and parser dependencies in the same environment.

## Source Repositories

Main source:

```text
/Users/raynording/DalamudActCompat
```

Custom repository metadata:

```text
/Users/raynording/DalamudActCompatRepo
```

Planned remote URLs:

```text
https://github.com/JackyWilliam/DalamudActCompat
https://github.com/JackyWilliam/DalamudActCompatRepo
https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json
```

## Windows Setup

1. Install Git.
2. Install .NET SDK 10.0.x.
3. Install FFXIV and XIVLauncher with Dalamud enabled.
4. Confirm Dalamud dev plugin loading works from `/xlsettings`.
5. Clone or copy both local repositories to Windows.
6. In the main source repository, run:

```powershell
dotnet restore DalamudActCompat.slnx
dotnet build DalamudActCompat.slnx -c Release
```

## Expected Current Result

- `DalamudActCompat.Host` should build.
- The plugin project may expose real Dalamud API compile errors on Windows. Fix those before adding parser integration.
- The parser will still report missing dependency because `IinactAdapter` is currently only the integration boundary.

## First Windows Tasks

1. Build the solution and fix compile errors.
2. Load the plugin as a dev plugin in Dalamud.
3. Verify `/actcompat`, `/actcompat settings`, `/actcompat history`, and `/actcompat status`.
4. Confirm plugin unload does not leak windows, commands, background tasks, or file handles.
5. Create/push the GitHub repositories after GitHub CLI authentication is fixed.
6. Run the GitHub Actions release flow and verify the raw custom repository URL.

## Current Known Blocks

- GitHub CLI token on the macOS machine is invalid.
- No release ZIP exists yet, so `pluginmaster.json` points to a planned `v0.1.0` artifact.
- IINACT/NotACT/FFXIV_ACT_Plugin runtime integration is not implemented yet.
