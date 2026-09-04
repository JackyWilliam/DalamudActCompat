# Dalamud ACT Compat

[简体中文](README.md) | [English](README_EN.md)

[Latest release](https://github.com/JackyWilliam/DalamudActCompat/releases/latest) · [Third-party compatibility](docs/third-party-compatibility.md) · [Report an issue](https://github.com/JackyWilliam/DalamudActCompat/issues)

**Use an in-game combat meter, Cactbot overlays, and common ACT extensions directly in FFXIV through Dalamud—without running ACT separately.**

Dalamud ACT Compat (DACT) combines `FFXIV_ACT_Plugin`, `OverlayPlugin`, and an in-game UI in one Dalamud plugin. Traditional ACT extensions such as Triggernometry, PostNamazu, and FoxTTS run in separate Host processes. If an extension hangs, you can restart its Host instead of immediately restarting the game.

The project is currently released and validated primarily on **Windows with XIVLauncherCN and Dalamud API 15**. It is not an official project of Square Enix, Dalamud, ACT, FFLogs, or any bundled third-party extension.

## What you can do with it

| Feature | What it is for |
| --- | --- |
| In-game combat meter | View 4/8/24-player DPS, rDPS, HPS, total damage, highest-hit actions, hit rates, deaths, and a local FFLogs percentile estimate |
| Meter styles | Choose one of Classic, Horizontal, or Role Split; customize ordered slots in Page preview while Horizontal remains background-free |
| Encounter history | Store runs and individual pulls, then inspect party data and raw logs |
| Cactbot / HTML overlays | Install Cactbot resources and create, resize, lock, or click through in-game overlays |
| Common ACT extensions | Use Triggernometry, PostNamazu, ACT.FoxTTS, SilverDasher, and Cafe.Matcha |
| Imported extensions | Preflight DLL/ZIP packages and load compatible ACT plugins in a generic Host after permission review |
| Accounts and cloud sync | Register with an activation key, optionally sign in automatically, create up to three friend invitations, and back up configuration with end-to-end encryption |
| Isolation and diagnostics | Inspect the parser, shared Host, Matcha Host, and generic Host separately, then stop or restart only the failing component |

DACT does not promise compatibility with every ACT plugin. Extensions that depend on old .NET Framework behavior, injection, arbitrary memory access, specialized ACT UI, or unimplemented APIs may not work.

## Installation

### 1. Add the custom plugin repository

In game, enter `/xlsettings`. Open **Experimental → Custom Plugin Repositories**, add the following URL, and save:

```text
https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json
```

### 2. Install the plugin

Enter `/xlplugins`, search for **Dalamud ACT Compat**, and select Install.

Starting with 0.3.10.0, Dalamud installs an approximately 18 MiB core package, then the plugin fetches only missing Host, Cactbot, and bundled-extension resources. Verified same-version caches are reused; interrupted downloads resume, and a failed update retains the previous verified resources. Do not manually extract resource packs into the plugin directory.

### 3. Obtain a one-time activation key

Registering a DACT account requires a one-time activation key. Join QQ group **1098561701** or the [Discord server](https://discord.gg/HpQZErSPc), then contact the group or server owner to request one.

> An activation key expires after use. Do not post a received key in an Issue or any other public location. In `0.4.0.1`, the in-game help button in the top-right corner is available only after sign-in; use this README during initial registration until that limitation is fixed in a later version.

### 4. Complete first-time setup

1. Read the third-party extension notice. Check each author, source, version, permission set, and SHA-256 before deciding whether to install and authorize it.
2. Enter `/actcompat` to open the Control Center.
3. On Overview, keep Enable parsing and Start parser automatically enabled. Confirm the status at the top is Running.
4. Enter `/actcompat meter`, then deal valid damage to a striking dummy or duty enemy to populate the meter.
5. Configure Cactbot, Triggernometry, PostNamazu, or other optional extensions from the Overlays and Extensions pages only when needed.

The help button in the top-right corner opens the complete in-game guide. It supports Chinese and English keyword search and lets you copy commands.

> Open FFLogs upload logs only opens the local Network log directory and copies its path. It does not upload automatically. Diagnostic logs are for troubleshooting and do not replace combat logs.

## Interface preview

![Control Center overview](docs/images/readme/control-center-overview.png)

| Combat Meter settings | Cactbot and HTML overlays |
| --- | --- |
| [![Combat Meter settings](docs/images/readme/combat-meter-settings.png)](docs/images/readme/combat-meter-settings.png) | [![Overlay settings](docs/images/readme/overlay-settings.png)](docs/images/readme/overlay-settings.png) |
| **Extension management** | **General settings** |
| [![Extension management](docs/images/readme/extension-management.png)](docs/images/readme/extension-management.png) | [![General settings](docs/images/readme/general-settings.png)](docs/images/readme/general-settings.png) |
| **Encounter history** | **Runtime status** |
| [![Encounter history](docs/images/readme/encounter-history.png)](docs/images/readme/encounter-history.png) | [![Runtime status](docs/images/readme/runtime-status.png)](docs/images/readme/runtime-status.png) |

> These screenshots show the v0.3.9.0 interface. Later versions keep the same information structure, but wording, version numbers, and individual options may change. Select a thumbnail to view the full image.

## Control Center

| Page | What you can do there |
| --- | --- |
| Overview | Inspect parser state; toggle parsing, autostart, unfocused web-overlay hiding, and Simplified mode; open the meter, history, runtime status, and log directory |
| Combat Meter | Select one of three mutually exclusive templates; customize the 8-player classic table, use fixed job/name + DPS/HPS rows for 24 players, and configure opacity or FFLogs where supported |
| Overlays | Install Cactbot, manage alerts, timelines, and custom HTML overlays, and hide web overlays when the game is unfocused |
| Extensions | Enable extensions, open their configuration, inspect sources and updates, import DLL/ZIP packages, and grant permissions |
| Cloud Sync | Register or sign in, choose automatic sign-in, manage friend invitations, and upload, preview, restore, or roll back end-to-end encrypted configuration |
| Settings | Auto-detect or manually select China/Global, restart the parser, copy diagnostics, change language and shortcuts, or start a separately confirmed factory reset |

Game region defaults to the native client-region value exposed by the game's Framework: the China client uses the China packet family and the international client uses Global. Dalamud language and installation paths are not used, so a Chinese language pack on the international client remains Global. If the game region cannot be read, DACT defaults to Global and allows a manual override. Changing the setting refreshes the running parser and extension Hosts without changing client-language action names or log parsing. The FFLogs estimate follows the same setting, using the CN partition for China and the latest worldwide partition for Global.

## Common commands

| Command | Action |
| --- | --- |
| `/actcompat` | Open or close the Control Center |
| `/actcompat on` / `/actcompat off` | Explicitly open / close the Control Center |
| `/actcompat meter` | Open and locate the Combat Meter |
| `/actcompat simple on` / `/actcompat simple off` | Enter / leave Simplified mode; its home page only toggles the meter or exits the mode |
| `/actcompat history` | Open recent encounters |
| `/actcompat logs` | Open the saved log-file list |
| `/actcompat status` | Inspect the parser and Host runtime state |
| `/actcompat cactbot` | Open the selected Cactbot overlay |
| `/actcompat overlay [template]` | Open the selected or named HTML overlay |
| `/actcompat clear` | Clear the current encounter display without deleting history or raw logs |

Diagnostic and development commands also include `/actcompat host`, `stop`, `sample`, `install "<DLL or ZIP path>"`, and `factory-reset`. An unknown subcommand opens the authoritative in-game command page.

## Understanding combat data

- **DPS** uses each player's own active action time.
- **EncDPS** uses the full encounter duration, closer to the traditional whole-encounter ACT metric.
- **rDPS** is estimated locally from combat events and party-buff attribution. It is not the authoritative FFLogs result.
- **HPS** uses the full pull duration from engagement to end, including transitions and untargetable periods.
- **Highest hit** keeps the largest single hit in the current encounter, replaces it only with a larger hit, and resets for the next encounter.
- **FFLogs percentile estimate** applies the current encounter data to cached curves for immediate reference. It is not an uploaded parse ranking.
- **`--`** means that no valid value is available yet; it does not mean zero.

After combat ends, the live meter retains the previous result until meaningful data from the next pull replaces it from zero. Duty wipes follow the same rule. Encounter History keeps the whole duty visit in one folder and stores each pull as a separate child record. ACT segments created during phase transitions are merged internally instead of appearing as separate pulls.

Reset current encounter ends and clears only the current display. It does not delete saved history or raw Network logs. Player-ID masking also changes only the UI and does not rewrite log contents.

## Extensions, permissions, and restarts

Enabling an extension and granting permissions are separate conditions. An enabled extension does not automatically have access to networking, file writes, game commands, native memory, or high-risk scripts.

| Component | Runtime | First action after an update or failure |
| --- | --- | --- |
| Triggernometry, PostNamazu, ACT.FoxTTS, SilverDasher | Shared Host | `/actcompat status` → Restart shared Host |
| Cafe.Matcha | Dedicated Matcha Host | `/actcompat status` → Restart Matcha Host |
| User-imported ACT extensions | Generic Host | `/actcompat status` → Restart generic Host |
| FFXIV_ACT_Plugin, OverlayPlugin | In-game parser | Control Center → Settings → Restart parser |

Traditional ACT extensions run with the current Windows user's privileges. Separate Hosts prevent most extension crashes from directly terminating FFXIV, but they are not operating-system sandboxes. A malicious DLL in the same Host may still call desktop APIs or interfere with another extension. Install only trusted packages and grant only the permissions you need.

See [Third-party ACT extension compatibility](docs/third-party-compatibility.md) for maintenance sources, bundled versions, installation models, and the currently verified scope.

## Troubleshooting

### `/actcompat` does nothing or is reported as unknown

Open `/xlplugins` and confirm that Dalamud ACT Compat is installed, enabled, and has no load error. In this state, the plugin's own restart and diagnostics controls cannot work; resolve the Dalamud load error first.

### The Combat Meter is missing or always empty

1. Enter `/actcompat meter` and temporarily disable Auto hide out of combat.
2. Confirm that the parser is Running on Overview and in `/actcompat status`.
3. Deal several valid hits to a striking dummy or duty enemy. Entering a duty, selecting a target, or opening the window does not create combat data.
4. If it remains empty, restart the parser and create a new sample encounter.

### Only one extension, TTS, trigger, or marker feature fails

Confirm that the extension itself is enabled and has the required permissions. Then use `/actcompat status` to restart only its assigned Host. Do not repeatedly reinstall downstream extensions while the parser is stopped.

### An overlay shows its page but receives no combat data

Loading a webpage does not mean its data protocol is connected. Confirm that the parser and OverlayPlugin are running, inspect the overlay's modern OverlayPlugin / ACTWS detection state, and select Detect again.

### What to include in a bug report

Provide the DACT version, occurrence time, reproduction steps, expected and actual behavior, the first failed component in `/actcompat status`, and the complete Copy diagnostics output. Missing or incorrect combat data also requires the matching `Network_*.log`. Review character names, server names, and local paths before sharing.

## How it runs

```text
FFXIV / Dalamud process
└─ Dalamud ACT Compat
   ├─ FFXIV_ACT_Plugin + OverlayPlugin
   ├─ Control Center, Combat Meter, history, and overlays
   ├─ accounts, invitations, and locally encrypted cloud configuration sync
   └─ bounded IPC
      ├─ Shared Host: Triggernometry / PostNamazu / FoxTTS / SilverDasher
      ├─ Matcha Host: Cafe.Matcha
      └─ Generic Host: compatible user-imported ACT extensions
```

The parser and OverlayPlugin remain in the game process so they can consume Dalamud data and render the UI directly. Traditional ACT extensions live in separate processes so they can be throttled, diagnosed, stopped, and restarted independently. IPC exposes defined messages and game-command operations instead of arbitrary function pointers.

## Development and build

You need Windows, the .NET 10 SDK, Git submodules, and Dalamud API 15 development files.

```powershell
git clone --recurse-submodules https://github.com/JackyWilliam/DalamudActCompat.git
cd DalamudActCompat
dotnet restore DalamudActCompat.slnx
dotnet build DalamudActCompat.slnx -c Release
```

GitHub Actions builds the complete solution on a Windows runner and executes the Package, Host, and Legacy Resource smoke tests. A formal release additionally synchronizes pinned dependencies, repackages the plugin, and validates the artifact in a Windows environment with XIVLauncher/Dalamud development files.

Implementation and release documentation:

- [Custom repository publishing](docs/CUSTOM_REPOSITORY.md)
- [Windows custom repository testing](docs/WINDOWS_CUSTOM_REPO_TEST.md)
- [ACT Host isolation design](docs/act-host-isolation.md)
- [Compatibility-layer investigation](docs/act-compat-phase2-investigation.md)
- [Third-party licenses and notices](THIRD_PARTY_NOTICES.md)

## License

Dalamud ACT Compat is distributed under [GPL-3.0](LICENSE.md). Bundled third-party components remain under their respective licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and the `LICENSES/` directory in release archives.
