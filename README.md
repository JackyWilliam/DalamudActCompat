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
  for update checks; SilverDasher stays on the disclosed fixed package and is
  installed disabled until the user explicitly enables it.
- Optional ACT plugin packages with manifest validation, safe ZIP extraction, atomic installation, upgrade backup, enable/disable composition, and isolated load contexts.
- Recoverable factory reset: mutable state is moved to a timestamped backup before default system plugins and settings are restored.

## 用户使用指南

游戏内点击主页右上角的帮助入口可打开完整“使用帮助”。帮助页支持中英文关键词搜索，可搜索功能名称、错误现象、扩展名称或 `/actcompat` 命令，并直接跳转到对应章节。

### 控制中心各页面

输入 `/actcompat` 切换控制中心的打开与关闭；`/actcompat on` 始终打开，`/actcompat off` 始终关闭。五个页面分别负责：

| 页面 | 主要用途 |
| --- | --- |
| 概览 | 查看解析器状态，开关解析和自动启动，打开战斗统计、历史、运行状态与 FFLogs 原始日志目录 |
| 战斗统计 | 显示或定位统计窗，设置锁定、穿透、自动隐藏、DPS/HPS 排序、DPS 口径、玩家显示与 FFLogs 预估 |
| 悬浮窗 | 安装和设置 Cactbot，从本地模板添加窗口，或从可信网址创建 HTML 悬浮窗 |
| 扩展 | 启停系统插件和 ACT 扩展，打开扩展配置，安装 DLL/ZIP，检查更新、来源、实际版本与权限 |
| 设置 | 重启解析器、查看详细状态、复制诊断日志、调整语言和快捷按钮，以及执行最后手段的恢复出厂设置 |

### 第一次使用

1. 阅读首次启动时显示的第三方扩展作者、来源、版本和权限，确认后才会安装并加载对应扩展。
2. 在主页保持“启用解析”和“自动启动解析器”开启；顶部显示“运行中”后再进入战斗。
3. 输入 `/actcompat meter` 打开战斗统计，输入 `/actcompat history` 查看已结束战斗，输入 `/actcompat status` 查看解析器和各 Host。
4. “打开 FFLogs 上传日志”只打开本地 Network 日志目录并复制路径，不会自动上传；诊断日志用于排错，不能代替 FFLogs 战斗日志。

### 战斗统计口径

- `DPS` 使用每名玩家自己的有效动作时长；`EncDPS` 使用整场战斗时长；`ExtDPS` 是保留给旧 ACT 扩展的兼容字段。
- `rDPS` 是基于本地战斗事件和团队增益归属的实时估算，不等于 FFLogs 服务器端权威结果。
- `DPS Parse` 把本场实际 DPS 代入同职业、副本、难度、区域和分区的缓存曲线；它不是上传后的正式排名。
- `HPS` 使用一把战斗从开怪到结束的完整经过时间，包括转阶段和目标不可选中的时间，不会套用排除阶段空档的伤害有效时长。伤害技能附带的自疗也会从原始效果中补充统计；没有新治疗时，累计治疗保持不变，但随着经过时间增加，HPS 会逐步下降。
- 暴击率、直击率和直暴率来自本场已发生的伤害命中样本，因此战斗中会随新命中变化。`--` 表示还没有有效值，不表示零分。
- 副本外在本队停止产生相关战斗数据 5 秒后清空实时统计，下一场战斗从 0 开始；队友先开怪时不会因本地角色仍处于脱战状态而反复清空。副本内则持续累计；普通脱战、阶段切换、击杀前置目标和 ACT 自动分段都不会清零。只有确认全队团灭后重新开怪才从 0 开始，即使副本存档从 P2 等中间阶段开始，也不会继承上一把。
- 历史记录以“一次副本进入”为一个可展开文件夹，文件夹内每条子记录代表一次团灭前累计的完整战斗。团灭重开会在同一文件夹新增下一条记录，但实时统计立即从 0 开始；退出副本后关闭该文件夹，下次进本才新建文件夹。同一把因转阶段产生的 ACT 片段只在内部合并，不会拆成多把。

第一次配置统计窗时，在“控制中心 → 战斗统计”开启“显示战斗统计”并点击“定位到战斗统计”。先关闭锁定，把窗口拖到需要的位置，再恢复锁定；要让点击传给游戏，同时开启“锁定时鼠标穿透”。“收起（只显示自己）”只隐藏队员行，不会停止统计。“显示列”可分别开关 FFLogs、DPS、HPS、暴击%、直击%、直暴%、伤害占比%和死亡；新配置默认显示 FFLogs、DPS、暴击%、直暴%、伤害占比%和死亡，HPS 与直击%默认关闭但仍可自行开启；FFLogs 只有在线预估已开启且该列已勾选时才显示。背景透明度统一控制窗口、标题、表头、玩家行和进度条底色，设为 `0` 时底色完全透明但文字和图标保留。玩家 ID 遮盖只影响界面显示，不会改写原始日志。“重置当前战斗”会结束并清空本把统计，同一底层战斗段的后续刷新不会让旧数值回弹；已保存的历史和原始 Network 日志不会被删除。

要启用 FFLogs DPS Parse 在线预估，在同一页面开启“启用 FFLogs 在线估算”，按“如何创建 FFLogs API Client”的说明创建免费 Client，填入 Client ID 和 Client Secret 后点击“测试并刷新”。Client Secret 只保存在本机配置中，不应截图或分享。无有效 DPS、缓存缺失、凭据错误或职业/副本无法映射时显示 `--`。

### 悬浮窗

- “编辑位置和大小”用于拖动和缩放；“完成位置编辑”会恢复位置锁定与鼠标穿透，把鼠标交还游戏。
- 要点击网页按钮或滚动网页，请单独关闭该悬浮窗的鼠标穿透；页面缩放和窗口大小是两项独立设置。
- 页面能显示不等于数据协议已连接。无数据时先确认解析器与 OverlayPlugin 运行，再查看现代协议/ACTWS 检测状态。

### 扩展更新与重启

扩展页显示入口 DLL 的实际 `FileVersion`。如果外部工具只替换 DLL、没有更新 `actcompat.plugin.json`，页面仍显示实际版本；悬停版本号可同时查看安装清单记录。清单继续用于来源、授权和哈希门禁。

传统 ACT 扩展更新后通常不需要重启整个游戏：

| 扩展 | DACT 中需要执行的操作 |
| --- | --- |
| Triggernometry、PostNamazu、ACT.FoxTTS、SilverDasher | `/actcompat status` → “重启共享 Host” |
| Matcha | `/actcompat status` → “重启抹茶 Host” |
| 自行导入的普通 ACT 扩展 | `/actcompat status` → “重启通用 Host” |
| FFXIV_ACT_Plugin / OverlayPlugin 系统状态变更 | 控制中心设置页 → “重启解析器” |

只有对应 Host 无法停止、或 DACT 本体更新无法正常重载时，才需要完整退出游戏和启动器。

### 启用扩展与开放权限

“启用扩展”和“开放权限”是两个独立条件。进入“控制中心 → 扩展”，先确认扩展已安装并勾选扩展名称；需要配置时点击同一行的“打开配置”。显示“已启用”不代表网络、文件、游戏指令、原生内存或高风险脚本等能力已经开放。

首次安装或内置扩展更新后的完整流程：

1. 打开“三方扩展声明”，核对作者、来源、版本和 SHA-256。
2. 点击“知悉并安装 / 更新”。
3. 安装完成后可选择“同意并启用完整权限”，也可保持安全模式，稍后按需逐项开启。

之后给内置扩展补权限：

1. 打开“控制中心 → 扩展”。
2. 滚动到“ACT 插件权限边界”。
3. 展开鲶鱼精、Triggernometry、银山雀儿或抹茶。
4. 只勾选实际功能所需的能力；保存后 DACT 会自动重启对应 Host。

自行导入的普通 ACT 插件如果显示“未授权”，在“用户安装的普通 ACT 插件”中点击“查看并授权”，核对静态预检清单后点击“授权并启用”。它随后在通用 Host 中运行。

常见权限与用途：

- 在线查询或更新：网络请求。
- 写入扩展缓存或导出到用户选择的文件：写入文件。Matcha 保存其专属目录内的自身配置不需要此权限。
- 鲶鱼精命令和标点：发送游戏指令；部分原版能力还需要访问游戏原生内存或调用 Windows 原生接口。
- Triggernometry 高级脚本：运行高风险脚本。

完整权限只表示允许扩展声明的全部能力，不表示第三方 DLL 已被证明绝对安全。DLL 仍可直接调用 Windows API，因此只安装可信来源，并优先按需授权。

### 排错顺序

先输入 `/actcompat status`，从解析器开始找到第一个不是“运行中/已连接”的层级，再重启对应组件。不要把恢复出厂设置作为第一步；该操作会把配置、日志、历史、悬浮窗和扩展目录移动到备份位置。

#### 插件打不开、命令没反应或一直初始化

1. 输入 `/actcompat`。如果提示命令不存在或控制中心完全打不开，到卫月插件安装器确认 Dalamud ACT Compat 已安装、已启用且没有加载错误。
2. 控制中心能打开时，在“概览”确认“启用解析”和“自动启动解析器”已勾选。
3. 解析器长时间为“已停止”“初始化中”或“错误”时，在“设置”点击“重启解析器”。
4. 输入 `/actcompat status`，先处理第一个失败的上游组件。
5. 仍失败时点击“复制诊断日志”，反馈版本、发生时间、操作步骤、预期结果、实际现象和错误原文。

#### 没有战斗统计、没有队员或统计窗不见了

1. 输入 `/actcompat meter`；或在“控制中心 → 战斗统计”开启显示并点击“定位到战斗统计”。排查时先关闭“脱战自动隐藏”。
2. 确认解析器为“运行中”，然后对木人或副本敌人实际造成几次有效伤害。进本、选中目标或只站在战斗区域不会生成统计。
3. “收起（只显示自己）”开启时不会显示队员；联盟其他小队、宠物和普通 NPC 本来就不会显示为独立玩家行。
4. 仍为空时重启解析器并产生一场全新的样本，不要先恢复出厂设置。
5. 问题继续存在时，同时保留当场原始 `Network_*.log` 和“复制诊断日志”的内容：前者用于检查战斗事件，后者用于检查 DACT 与解析器状态。

#### 只有某个扩展、TTS、触发器或标点不能用

1. 在“扩展”页确认它已安装、已启用且所需权限已开放；普通插件若显示“未授权”，先执行“查看并授权”。
2. 点击“打开配置”，确认功能在扩展内部也已启用。Triggernometry 需要已导入并启用触发器；FoxTTS 需要可用语音服务、音色和输出设备。
3. 在 `/actcompat status` 中只重启它所属的 Host。共享 Host 负责 Triggernometry、PostNamazu、FoxTTS 和 SilverDasher；Matcha 与普通导入插件分别使用抹茶 Host 和通用 Host。
4. 扩展错误优先复制失败窗口的完整日志；解析器或界面问题使用“设置 → 复制诊断日志”。不要只截错误最后一行。

#### 悬浮窗有页面但没有数据

先确认解析器与 OverlayPlugin 正在运行，再查看该窗口的自动协议检测状态并点击“重新检测”。网页能够打开不代表现代 OverlayPlugin 或 ACTWS 数据协议已经连接；外部网页还可能要求网络访问或自身配置。

#### 反馈问题需要的材料

至少提供 DACT 版本、发生时间、复现步骤、预期与实际结果、`/actcompat status` 中第一个失败组件和完整诊断日志。战斗统计缺失、数值异常或 FFLogs 差异还需保留对应的原始 Network 日志。发送前自行检查角色名、服务器名和本地路径。

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
- `DalamudActCompatRepo` checks this repository's latest public Release every 15 minutes and updates its own `pluginmaster.json` with its repository-scoped `GITHUB_TOKEN`. No cross-repository secret is required.

## Run

Build the plugin, then add the output DLL path to Dalamud dev plugin locations from `/xlsettings`. Load the dev plugin and use:

```text
/actcompat
/actcompat on
/actcompat off
/actcompat meter
/actcompat history
/actcompat logs
/actcompat status
/actcompat cactbot
/actcompat overlay [template]
/actcompat sample
/actcompat clear
/actcompat host
/actcompat stop
/actcompat install "C:\path\plugin.zip"
/actcompat factory-reset
```

`/actcompat` toggles the plugin control center, `/actcompat on` always opens it,
and `/actcompat off` always closes it. `/actcompat meter` opens Combat Meter. The in-game Help window contains the
complete command reference with a clipboard-copy button for every entry.

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
