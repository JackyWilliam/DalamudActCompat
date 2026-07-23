# Dalamud ACT Compat

Dalamud ACT Compat is a self-contained in-game ACT-compatible plugin host. Users install one Dalamud plugin; it directly hosts the official `FFXIV_ACT_Plugin`, embeds `OverlayPlugin`, and can install optional ACT-compatible plugin packages inside its own managed directory.

## Architecture Audit

1. Current project type: no existing C# repository was present at `/Users/raynording`; this directory creates a new Dalamud plugin project plus an out-of-process compatibility host.
2. Dalamud reference: the plugin uses `Dalamud.NET.Sdk/15.0.0`, matching the current Dalamud API Level 15 release framework.
3. ACT compatibility runtime: the project vendors a pinned IINACT fork for its proven NotACT shim, FFXIV_ACT_Plugin boot kernel, and OverlayPlugin runtime. The IINACT Dalamud entrypoint, UI, IPC facade, and branding are not started.
4. .NET/API version: `net10.0-windows`, Dalamud API Level 15 minimum. Windows is the correct validation target for this project because the ACT compatibility host, WinForms shims, FFXIV_ACT_Plugin, and XIVLauncher/Dalamud development files are Windows-first.
5. Code to keep: the new immutable combat data model, parser abstraction, state store, history repository, Meter UI, settings/status windows, Overlay event bus, and host boundary.
6. Compatibility boundary: optional packages must explicitly target the DalamudActCompat host ABI. Legacy .NET Framework ACT plugins are not claimed compatible until tested.
7. Missing modules: translating official parser events into the native Meter model, FFLogs-quality log output, Cactbot integration, and a graphical package picker.
8. System plugins: `FFXIV_ACT_Plugin` and `OverlayPlugin` are independently visible in settings. Factory reset restores both to their default enabled state.
9. License notes: the IINACT/NotACT integration is GPL-3.0; applicable upstream licenses and notices are included in release archives.
10. Realistic MVP: a stable Dalamud plugin shell with lifecycle cleanup, parser state reporting, game-internal Meter/history/settings UI, persistent history/config/log directories, and a clearly isolated compatibility host integration point.

## Current Features

- Dalamud plugin lifecycle with command `/actcompat`.
- Native ImGui Meter window with DPS/HPS columns, damage percent, death count, duration, sorting, local-player highlight, lock, opacity, font scale, click-through flag, auto-hide flag, and reset.
- Encounter history window backed by plugin config storage.
- Settings window for parser enablement, autostart, Meter settings, history limit, debug flag, parser status, parser restart, and log directory.
- Parser status model: disabled, initializing, running, stopped, missing dependency, incompatible, faulted.
- In-process official FFXIV_ACT_Plugin runtime using the NotACT compatibility assembly.
- Embedded OverlayPlugin runtime initialized against the same ACT host.
- Optional ACT plugin packages with manifest validation, safe ZIP extraction, atomic installation, upgrade backup, enable/disable composition, and isolated load contexts.
- Recoverable factory reset: mutable state is moved to a timestamped backup before default system plugins and settings are restored.

## Build

Use a Windows machine with .NET 10 SDK and XIVLauncher/Dalamud API 15 development files, then run:

```bash
dotnet build DalamudActCompat.slnx
```

Local validation status:

- .NET SDK 10.0.302 was installed under `~/.dotnet`.
- `dotnet restore DalamudActCompat.slnx` succeeds when NuGet network access is available.
- `src/DalamudActCompat.Host/DalamudActCompat.Host.csproj` builds successfully.
- `0.1.8` targets Dalamud API Level 15 and packages the parser, OverlayPlugin, compatibility assembly, SDK modules, and runtime dependencies in one ZIP.
- Windows Release build succeeds with XIVLauncherCN/Dalamud development files at `C:\Users\jacky\AppData\Roaming\XIVLauncherCN\addon\Hooks\Dev\`.
- The release collector verifies the plugin manifest, packaged Host executable, and all four embedded Host resources.

GitHub Actions are included for Windows-based CI:

- `.github/workflows/build.yml` validates metadata and builds the out-of-process host on GitHub-hosted runners. It intentionally does not build the Dalamud plugin project because `Dalamud.NET.Sdk` requires local XIVLauncher/Dalamud dev files.
- `.github/workflows/release.yml` is reserved for a Windows self-hosted runner with XIVLauncher/Dalamud installed.
- `.github/workflows/sync-custom-repo.yml` updates the separate custom repository file. It requires a `DALAMUD_REPO_TOKEN` secret with write access to `JackyWilliam/DalamudActCompatRepo`.

## Run

Build the plugin, then add the output DLL path to Dalamud dev plugin locations from `/xlsettings`. Load the dev plugin and use:

```text
/actcompat
/actcompat history
/actcompat settings
/actcompat status
/actcompat sample
/actcompat clear
/actcompat host
/actcompat stop
/actcompat install "C:\path\plugin.zip"
/actcompat factory-reset
```

`/actcompat sample` loads a local fake encounter to validate the snapshot-to-Meter UI path. It is development data only and does not come from ACT, IINACT, or FFXIV_ACT_Plugin.

`/actcompat host` starts the embedded ACT host, official FFXIV_ACT_Plugin, enabled OverlayPlugin runtime, and enabled optional packages. `/actcompat stop` unloads them in reverse order.

Optional package ZIPs contain `actcompat.plugin.json` at their root:

```json
{
  "id": "example.plugin",
  "name": "Example ACT Plugin",
  "version": "1.0.0",
  "entryAssembly": "Example.Plugin.dll",
  "entryType": "Example.Plugin.EntryPoint",
  "hostApiVersion": 1
}
```

Factory reset is confirmed in Settings. It stops the host and moves the existing configuration, logs, history, overlays, and optional plugin directory into `factory-reset-backups/<timestamp>` before recreating defaults.

## Custom Repository

The planned custom repository raw URL is:

```text
https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json
```

The template store entry lives at `repo/pluginmaster.json`. Users add the raw JSON URL to Dalamud; the JSON points to the plugin ZIP in GitHub Releases. See `docs/CUSTOM_REPOSITORY.md` for the release flow.

For Windows-side testing from a custom repository, follow `docs/WINDOWS_CUSTOM_REPO_TEST.md`.

## Current Limits

- No live combat parsing has been verified in game.
- FFLogs-compatible combat log output is directory-separated but not implemented.
- OverlayPlugin is embedded, but its HTML/WebSocket behavior still needs an in-game validation pass.
- Optional packages run in-process and must target host API version 1. Arbitrary legacy ACT plugins may depend on unsupported .NET Framework or ACT UI behavior.

## Next Stage

Validate the self-hosted parser and OverlayPlugin in game, translate `DataSubscription` events into `EncounterSnapshot`, then add a graphical package installer and compatibility diagnostics for legacy ACT plugins.
