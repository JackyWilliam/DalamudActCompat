# Dalamud ACT Compat

Dalamud ACT Compat is the first-stage foundation for an in-game ACT-compatible platform. It is not an external ACT WebSocket meter and it does not currently claim full FFXIV_ACT_Plugin runtime support.

## Architecture Audit

1. Current project type: no existing C# repository was present at `/Users/raynording`; this directory creates a new Dalamud plugin project plus an out-of-process compatibility host.
2. Dalamud reference: the plugin uses `Dalamud.NET.Sdk/14.0.1`, matching the current Dalamud v14 guidance for .NET 10.
3. IINACT/NotACT/OverlayPlugin code: none existed locally. This implementation provides adapter boundaries and protocol placeholders only.
4. .NET/API version: `net10.0-windows`, Dalamud API v14 minimum. Windows is the correct validation target for this project because the ACT compatibility host, WinForms shims, FFXIV_ACT_Plugin, and XIVLauncher/Dalamud development files are Windows-first.
5. Code to keep: the new immutable combat data model, parser abstraction, state store, history repository, Meter UI, settings/status windows, Overlay event bus, and host boundary.
6. Code to refactor later: `IinactAdapter` must be replaced with a real IINACT/NotACT-backed host bridge; the host process currently only boots and waits.
7. Missing modules: real FFXIV_ACT_Plugin loading, ACT compatibility object model, named-pipe protocol, log-line ingestion, FFLogs-quality log writer, OverlayPlugin renderer/runtime, Cactbot bridge, TTS, plugin dependency resolver.
8. IINACT reuse candidates: NotACT ACT API shims, FFXIV_ACT_Plugin boot sequence, parser status handling, overlay event mapping, and any existing Dalamud network integration. Preserve upstream license notices before copying code.
9. License notes: cactbot is Apache-2.0; Dalamud is AGPL-3.0; DalamudPackager is EUPL-1.2; FFXIV_ACT_Plugin releases are distributed as binaries with public SDK assemblies but source is not public; OverlayPlugin and IINACT licenses must be checked from their repositories before code reuse.
10. Realistic MVP: a stable Dalamud plugin shell with lifecycle cleanup, parser state reporting, game-internal Meter/history/settings UI, persistent history/config/log directories, and a clearly isolated compatibility host integration point.

## Current Features

- Dalamud plugin lifecycle with command `/actcompat`.
- Native ImGui Meter window with DPS/HPS columns, damage percent, death count, duration, sorting, local-player highlight, lock, opacity, font scale, click-through flag, auto-hide flag, and reset.
- Encounter history window backed by plugin config storage.
- Settings window for parser enablement, autostart, Meter settings, history limit, debug flag, parser status, parser restart, and log directory.
- Parser status model: disabled, initializing, running, stopped, missing dependency, incompatible, faulted.
- IINACT adapter boundary and IPC client placeholder.
- OverlayPlugin-compatible event bus placeholder for `CombatData`, `LogLine`, `ChangeZone`, `ChangePrimaryPlayer`, `PartyChanged`, and `BroadcastMessage`.
- Out-of-process compatibility host project with exact `IActPluginV1` signature reserved for future ACT plugin loading.

## Build

Use a Windows machine with .NET 10 SDK and XIVLauncher/Dalamud v14 development files, then run:

```bash
dotnet build DalamudActCompat.slnx
```

Local validation status:

- .NET SDK 10.0.302 was installed under `~/.dotnet`.
- `dotnet restore DalamudActCompat.slnx` succeeds when NuGet network access is available.
- `src/DalamudActCompat.Host/DalamudActCompat.Host.csproj` builds successfully.
- The Dalamud plugin project cannot build on this machine yet because `Dalamud.NET.Sdk` cannot find a local Dalamud installation at `~/Library/Application Support/XIV on Mac/dalamud/Hooks/dev/`.

The current macOS workspace is useful for source editing and host-level checks, but full plugin validation should happen on Windows. See `docs/WINDOWS_HANDOFF.md`.

GitHub Actions are included for Windows-based CI:

- `.github/workflows/build.yml` builds branches and pull requests and uploads `DalamudActCompat.zip`.
- `.github/workflows/release.yml` builds and publishes a GitHub Release when a `vX.Y.Z` tag is pushed.
- `.github/workflows/sync-custom-repo.yml` updates the separate custom repository file. It requires a `DALAMUD_REPO_TOKEN` secret with write access to `JackyWilliam/DalamudActCompatRepo`.

## Run

Build the plugin, then add the output DLL path to Dalamud dev plugin locations from `/xlsettings`. Load the dev plugin and use:

```text
/actcompat
/actcompat history
/actcompat settings
/actcompat status
```

## Custom Repository

The planned custom repository raw URL is:

```text
https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json
```

The template store entry lives at `repo/pluginmaster.json`. Users add the raw JSON URL to Dalamud; the JSON points to the plugin ZIP in GitHub Releases. See `docs/CUSTOM_REPOSITORY.md` for the release flow.

For Windows-side testing from a custom repository, follow `docs/WINDOWS_CUSTOM_REPO_TEST.md`.

## Current Limits

- FFXIV_ACT_Plugin is not loaded yet.
- No live combat parsing has been verified in game.
- FFLogs-compatible combat log output is directory-separated but not implemented.
- HTML Overlay, WebSocket Overlay, Cactbot, TTS, and arbitrary ACT plugins are not supported yet.
- Third-party ACT plugin loading must remain out-of-process unless a specific module is audited as safe for the game process.

## Next Stage

Integrate IINACT/NotACT into `DalamudActCompat.Host`, define the named-pipe protocol, translate parser events into `EncounterSnapshot`, and add version checks plus dependency diagnostics before enabling real parsing.
