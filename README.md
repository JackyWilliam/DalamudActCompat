# Dalamud ACT Compat

第三方组件的维护来源、安装模型和已验证兼容范围见
[第三方 ACT 扩展兼容说明](docs/third-party-compatibility.md)。

Dalamud ACT Compat is a hybrid ACT-compatible runtime. The game process keeps the
Dalamud data collector, parser/Overlay runtime, status UI, IPC client, and a small
validated command broker. Traditional ACT plugins run in
`DalamudActCompat.Host.exe`, so a hung Triggernometry/PostNamazu/legacy plugin can
be terminated without terminating FFXIV.

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
- Native ImGui Meter window restricted to the local player/current party, with `UserName@ServerName`, live jobs, supplemented death detection, a DPS/HPS primary rate column that follows sorting, damage percent, duration, local-player highlight, lock, opacity, font scale, click-through flag, auto-hide flag, and reset.
- Encounter history window backed by plugin config storage.
- Settings window for parser enablement, autostart, Meter settings, history limit, debug flag, parser status, parser restart, and log directory.
- Parser status model: disabled, initializing, running, stopped, missing dependency, incompatible, faulted.
- In-process official FFXIV_ACT_Plugin parser and embedded OverlayPlugin runtime.
- Independent `DalamudActCompat.Host.exe` for the NotACT compatibility layer,
  WinForms, Triggernometry, PostNamazu, FoxTTS, and other manifest-based legacy
  ACT plugins. The game process does not load those traditional plugin DLLs.
- Versioned, current-user-only named-pipe IPC with bounded control/data queues,
  ordered log batches, state replay, deadlines, heartbeats, progress detection,
  crash restart, and a restart circuit breaker.
- Per-plugin capability controls and an allowlisted semantic game-command broker;
  no function pointer, memory address, PowerShell, or arbitrary-code IPC command.
- Structured Host/plugin diagnostics, callback duration/exception health,
  PostNamazu staged connection state, and game-side controls to stop or restart
  the Host.
- FFXIV 7.55 parser stack: IINACT 2.10.3.5, OverlayPlugin Core 0.19.104,
  Machina 7.55h opcodes, Unscrambler.XIV 7.55.1, and FFXIV_ACT_Plugin 3.0.2.7.
  The Chinese 7.55h client combines the complete 7.55h1 opcode definitions with
  its runtime-discovered regional key table; unsupported future versions raise an
  explicit warning before fallback parsing.
- Triggernometry CN 2.1.2.2, ACT.FoxTTS 3.3.1.189, PostNamazu 1.3.6.6, and the
  complete hash-pinned SilverDasher 0.6.0.4 package are bundled with the
  release. Their author, maintainer, version, permission/license status,
  project/source URL, and SHA-256 are shown on first install and after every
  DalamudActCompat update. The first three use their registered author sources
  for update checks; SilverDasher stays on the disclosed fixed package.
- Optional ACT plugin packages with manifest validation, safe ZIP extraction, atomic installation, upgrade backup, enable/disable composition, and isolated load contexts.
- Recoverable factory reset: mutable state is moved to a timestamped backup before default system plugins and settings are restored.

## Build

Use a Windows machine with .NET 10 SDK and XIVLauncher/Dalamud API 15 development files, then run:

```bash
dotnet build DalamudActCompat.slnx
```

Before a release, synchronize and verify the pinned parser and bundled ACT
plugin binaries:

```powershell
./tools/sync-parser-dependencies.ps1
./tools/sync-bundled-act-plugins.ps1
./tools/sync-parser-dependencies.ps1 -Check
./tools/sync-bundled-act-plugins.ps1 -Check
```

Local validation status:

- .NET SDK 10.0.302 was installed under `~/.dotnet`.
- `dotnet restore DalamudActCompat.slnx` succeeds when NuGet network access is available.
- `src/DalamudActCompat.Host/DalamudActCompat.Host.csproj` builds successfully.
- `0.1.14` targets Dalamud API Level 15 and adds a Chinese combat-log fallback so synchronized CN clients populate the in-game meter even when legacy regional opcodes are stale.
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

`/actcompat host` starts the independent legacy ACT Host and restarts the
in-process parser. `/actcompat stop` stops parsing and terminates the Host. The
Host also starts automatically; its heartbeat and plugin stages are visible in
`/actcompat status`.

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

- No live combat parsing or PostNamazu game-command execution has been verified
  in game yet. The offline real-plugin test covers Triggernometry configuration,
  standard/network-equivalent log matching, zone restrictions, combat start/end,
  game-side TTS authorization plus isolated Host output dispatch, clean shutdown,
  broken pipes, and Host process death. Actual audio-device output still needs a
  live manual check.
- FFLogs-compatible combat log output is directory-separated but not implemented.
- OverlayPlugin starts its WebSocket event source and opens installed Cactbot raidboss resources in a topmost WebView2 window; encounter alerts and TTS still require an in-game validation pass.
- The legacy Host is a full-trust desktop process, not an OS sandbox. Process
  isolation protects FFXIV from a Host crash/deadlock/runaway allocation, but a
  malicious plugin in the same Host can impersonate another plugin or directly
  use desktop APIs. Strong per-plugin enforcement requires one restricted worker
  process/AppContainer per trust domain.
- OverlayPlugin and the official parser still run in the game process; migrating
  or further reducing that trusted core is separate follow-up work.
- Arbitrary legacy ACT plugins may still depend on unsupported .NET Framework,
  ACT UI, packet capture, memory, or injection behavior.

## Next Stage

Perform the live-game validation matrix, finish the remaining PostNamazu semantic
actions, and then split untrusted legacy plugins into independently restartable,
restricted workers where stronger per-plugin security is required. See
[`docs/act-host-isolation.md`](docs/act-host-isolation.md) and
[`docs/act-compat-phase2-investigation.md`](docs/act-compat-phase2-investigation.md).
