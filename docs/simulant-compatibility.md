# Simulant（仿生石）兼容适配

DACT 只提供用户自行导入的 Simulant 的运行时适配（0.4.3.0 起支持 v0.0.4.2，1.0.0.10 起增加 v0.1.0.0，现已增加 v0.4.0.0）。未安装时不显示入口、推荐或专属权限项；产品不下载、不捆绑 Simulant。

## 用户手动导入

1. 用户自行取得原版 `Simulant.dll`，通过 DACT 现有「安装 DLL / ZIP」导入。
2. 手动导入后弹出授权提示，确认前保持禁用；确认后才进入对应 Host。扩展页提供启停、打开原版配置及垃圾桶图标删除按钮。删除前正常停止对应 Host，原文件移入备份目录。Simulant 在共享 Host 中排在 PostNamazu、MnFeN Triggernometry 两项依赖之后加载。
3. 在同页权限区启用 Simulant 的「原生游戏内存」，以及 PostNamazu 的「游戏命令」「原生游戏内存」。原有完整权限决定不会自动为 Simulant 授权。
4. 打开原版配置，CSV 加载完成后由用户手动初始化。安装、启用、打开配置均不会自动初始化或开启防火墙。
5. 签名扫描出现失败或空结果时，初始化保持失败，原版界面显示具体地址错误。
6. 退出游戏前先停止模拟，再取消“启用防火墙”，确认收发包恢复后退出游戏。“退出模拟”仅返回真实区域，不会关闭防火墙。

## 已验证版本与实现

离线验证使用上游 v0.0.4.2，DLL 大小 28,793,856 字节，SHA-256：
`12F31A446D6F137A7D0B39939F9E0361240D9209AC3677624FF95431C2578467`。
安装器保留用户原始 DLL，Host 在加载时转换旧版 WinForms NRBF 资源，继续禁用 BinaryFormatter；内嵌 CSV 与原版模拟逻辑保留。

FFXIV facade 同步绑定，替换 ACT 状态标签无限轮询及对应的无效退订。卸载仍执行原版防火墙恢复，最多等待 5 秒，再卸载 PostNamazu。
原生初始化和防火墙启用同时要求 Simulant 自身原生内存权限与 PostNamazu 的两项原生运行权限。

函数入口被已有 Hook 改写时，原版基于实时内存的扫描可能找不到 OnSendPacketFuncPtr / ActionManager.UpdateFuncPtr。仅对这两项失败增加原始游戏映像回查：PE 头和映像大小必须一致（ImageBase 只允许原值或实际 ASLR 基址），签名必须唯一，运行时函数体必须保持一致，入口只接受已知 E9 / FF25 / FF2425 跳转。其他不匹配仍中止初始化。
发包防火墙把已有跳转转换为指向原目标的绝对跳转后放入代码洞，保留 Hook 链；用于恢复入口的原始备份不变。代码洞分配在入口附近，入口只写入 5 字节 E9，避免原版 15 字节覆盖破坏已有 Hook 返回到 +7 / +10 的位置。开启前检查入口，先安装发包 Hook；后续失败时恢复已修改的收发包入口。
2026-09-14 实机确认修复后的地址扫描为 64/64，初始化成功。首次防火墙测试暴露了 15 字节覆盖与已有回跳位置冲突导致的崩溃；短入口修订通过测试进程内真实执行的心跳放行、普通包拦截和恢复回归。用户已确认修订版初始化和防火墙开启成功；只读检查确认 5 字节发包入口及收包拦截补丁生效。02:59:02.8 用户日志显示收发包 Hook 和防火墙均关闭，随后只读快照确认原有发包 Hook 和收包入口指令恢复，游戏进程仍在运行。

地图列表开头是上游预留的空名称记录。勾选“仅显示含预设区域”可显示 755、1122、1238、1363 四个区域；已验证 1122 的两个模拟预设可选择。按用户要求保留原版默认筛选行为。
用户另反馈实机勾选筛选时窗口短暂无响应，随后明确确认已自行恢复正常；暂未确定卡顿原因，未据此修改筛选逻辑。

开始按钮现在要求初始化、防火墙和模拟区域均就绪，阶段切换已结束，且游戏当前地图与预设一致。原版只凭已选预设即可启动，曾在进入副本前或返回旅店后仍显示“模拟已开始”。不满足条件时保留当前会话并提示原因，不自动切换地图；加载区域时新增请求日志。修订版实机于 03:13:04 收到加载 1122 请求，随后只读检查确认游戏地图由 177 变为 1122；用户明确说明此前是操作问题。用户随后确认欧米茄绝境 P6 宇宙天箭正常，日志包含先内侧和先外侧多次启动、停止。

原版 v0.0.4.2 的 P3 屏幕探测描述为“占位符”，`U6b3_屏幕探测Logic.OnStart()` 和 `OnStop()` 均直接抛出 `NotImplementedException`；已核对原版 DLL 的 IL 与源代码，和实机错误一致。这是上游尚未实现的预设，DACT 兼容适配不包含补写其机制；P3 地图/阶段加载与 P3 机制可用性应分别判断。

绝伊甸 P5 地火曾在生成 EObj 后立即查询实体时报找不到对象，导致整条时间轴中断；只读检查确认游戏中已有 BaseId 2014199 的场景物件。Host 实体增量约每 30 毫秒同步，无法满足原版生成后立刻回查的要求。原生扫描完成后，Simulant 的实体枚举改用原版 GameObjectManager / GameObjectPtrs 直接读取即时列表，保留自身及 PostNamazu 的权限检查；扫描前仍使用 Triggernometry 原有列表。回归通过原版 DLL 和托管内存夹具验证无 IPC 更新时新 EObj 可立即查询、撤销权限后不读取。53f5306 修订已覆盖本机，重启后确认实际 Host 为该修订并进入区域 1238；用户随后明确反馈“没问题了”，绝伊甸 P5 地火本轮实测通过。

上游发布说明标注 7.55。本机国服 `2026.09.01.0000.0000`（7.56）的磁盘 `.text` 节中，原版 DLL 的 64 组签名全部存在唯一匹配；这是静态检查，不验证运行时内存布局、地图切换、实体生成或防火墙恢复。
上游目前没有声明许可证，未确认作者对适配的授权；DACT 仅提供兼容代码，不重新分发其源码或 DLL。

## v0.1.0.0（已适配，尚未在游戏内验证）

上游在 2026-11-14 发布 v0.1.0.0（DLL 29,295,104 字节，SHA-256
`00D06324641A00DC087D52BD4F2D7C55E5598CD92AF944207AC7C6C1F6F6A81F`）。对照 v0.0.4.2 共 40 个提交、208 个文件：
防火墙改为按 Hook 记录状态（`HookState`），卸载流程重写，新增进程与区域订阅、自动更新、全局配置文件，
所有原生调用改走鲶鱼精邮差的调用调度器。

DACT 从 1.0.0.10 起同时支持 v0.0.4.2 和 v0.1.0.0，按程序集版本选择对应的一组补丁；高于 v0.1.0.0 的版本仍然
不加载并说明原因。**以下适配只做了静态验证（改写后检查 IL，不加载、不运行新版 DLL），初始化、防火墙开启
与关闭、切图、小队模拟都还没有在游戏里验证过。**

### 鲶鱼精邮差

v0.1.0.0 要求邮差 1.3.6.7：检查程序集版本，并反射读取 `State` 和 `CallScheduler`，原生调用全部经
`PostNamazu.Call / DirectCall / ExecuteInFrameLock`。这些接口上游在 2026 年 5 月合入 master，但正式发布仍是
不含它们的 1.3.6.6，仓库里也没有 1.3.6.7 这个版本号。

- DACT 随包的邮差改为从上游 master（提交 `521007c86343782c6bc9a52d5e192e520fcf32ca`）按上游 CI 的步骤自行构建，
  构建脚本是 `tools/build-postnamazu-snapshot.ps1`。它的类型和 `PostNamazu` 类成员与外面流通的 1.3.6.7 构建
  完全一致，版本号保持源码里声明的 1.3.6.6，DACT 不替上游编造版本号。
- 因此仿生石的版本要求由 1.3.6.7 放宽为 1.3.6.6；没有调度器的邮差仍会被上游自己对 `CallScheduler` 的检查拒绝。
- 随包门禁（`tools/sync-bundled-act-plugins.ps1`）固定这份构建的哈希，并关闭它的在线更新（官方 1.3.6.6 版本号
  相同，在线检查会把没有调度器的官方包换回来）。上游一旦发布比 1.3.6.6 更新的正式版，门禁会失败，提醒换回官方包。
- 改写器对邮差的适配本来就同时接受 1.3.6.6 与 1.3.6.7 两种结构，这份构建按后者处理，离线回归全部通过。

### 与 v0.0.4.2 不同的补丁

| 位置 | v0.1.0.0 的做法 |
| --- | --- |
| `PluginHost.Attach` | 仍替换为直接绑定（上游要等 ACT 状态标签出现 STARTED，门面上永远等不到）。上游在同一处订阅区域变化，用来让已生成实体和模拟小队的地址失效；门面不发这个事件，改由 DACT 把自己收到的区域变化转给上游的处理函数。进程变化不需要转发：Host 不会比游戏进程活得更久 |
| `PluginHost.Dispose` | 不再需要补丁。上游只退订它记录为已订阅的对象，而现在没有任何订阅 |
| `FirewallService.SendHookEnableCore` | 三处入口补丁按新结构重做，作用不变：已有跳转改为指向原目标的绝对跳转后放入代码洞、代码洞就近分配、入口只写 5 字节。上游仍然抄 15 字节并覆盖 15 字节 |
| `FirewallService.Enable` | 保留上游新增的失败回滚，只在最前面检查发包入口是否是可处理的形态，先于任何写入 |
| `NamazuInterop.Init` | 权限检查不变；版本要求放宽见上 |
| `PluginUpdater.RunAsync` | 改为什么都不做。上游每次启动检查 GitHub，默认还会原地替换自己的 DLL——在 DACT 里那是用户导入并授权了原生内存的那一份，而更新的版本在适配前本来就不会被加载 |
| `PluginHost` 的配置路径 | 由程序目录改到 ACT 数据目录。Host 的程序目录按 DACT 版本区分，配置会随 DACT 更新丢失 |

签名校验、开始模拟前的就绪检查、加载区域的日志、实体即时读取四处补丁两个版本通用。上游新增了
`GetStartBlockReason`、`EnableWithRollback` 和 `EntityRegistry`，其中后两项已经让 DACT 少做了事，前一项和实体读取
是否还需要 DACT 的补丁，留到游戏内验证时再判断。

### 已知情况

- `SuspendNetworkTimeout` 反射解析插件的私有字段 `_dataCollection._monitor`。DACT 的门面没有这些字段，调用会抛出；
  上游已捕获并只记一条警告，不影响开启防火墙。
- 仿生石界面里的检查更新、安装更新按钮在 DACT 下不起作用。要换版本，请删除扩展后重新导入 DACT 已适配的版本。
- 触发器里如果按「邮差版本 ≥ 1.3.6.7」判断是否可用调度器接口，在 DACT 下会判断为不满足，尽管接口是有的。
- 新增的原生功能（模拟小队写入 GroupManager / UIState、UpdateParty / UpdateHate 包、网络实体生成、场地标点）
  全部经邮差的内存接口执行，只能在游戏内验证。
- 上游主分支在 v0.1.0.0 之后又有十几个提交，引入 Lumina 读取游戏资源并删除了内嵌 CSV，下一个版本需要重新核对。
  v0.1.0.0 标签里没有许可证文件；主分支随后加入了 GPL-3.0 的 LICENSE。

## v0.4.0.0（已适配，离线验证与本机游戏内实测通过）

上游 v0.4.0.0（DLL 5,001,728 字节，SHA-256
`9597A5497FFAB6F784B85883CED89D35CC37FF5FCA9CAE4E1F75DC7EF7C1CF5F`）不再依赖 PostNamazu 与 Triggernometry 的实体接口：
改用自带的混合模式 `SimuMagic`（由 `GreyMagic` 改名而来，经 Costura 内嵌为 `costura64.simumagic.dll`）直接附加游戏进程，
通过磁盘 EXE 扫描地址、Lumina 读取客户端游戏数据，并直接读取 `GameObjectManager` 实体表。

DACT 按程序集版本（`0.4.0.0`）走独立适配分支，同时保留 `v0.0.4.2` 与 `v0.1.0.0` 的原有补丁；未核对的中间版本（`0.1.0.1`～`0.3.x`）与高于 `0.4.0.0` 的版本均在加载前拒绝并说明原因。

| 位置 | v0.4.0.0 的做法 |
| --- | --- |
| `costura64.simumagic.dll` | 与 PostNamazu 的 `GreyMagic` 相同，`SimuMagic.Executor` 调用了 .NET Framework 专属的五参 `EventWaitHandle` 构造器。加载 Simulant 前先以 `dnlib` 改写为 `EventWaitHandleAcl.Create` 并预加载到同一 `AssemblyLoadContext` |
| `PluginHost.RefreshXivPlugin` | 上游每秒轮询检查 `FFXIV_ACT_Plugin.PluginStarted` 与 `DataSubscription`。改为幂等绑定 `XivPluginInterop.plugin` 并通过 `SimulantCompatibility.ObserveZoneChanges` 接收 DACT 的切图通知；`_subscribedXivPlugin` 保持为 `null`，避免 `Dispose` 访问空 `DataSubscription` |
| `PluginHost.ReadGameProcess` / `UpdateGameProcess` / `RetryGameConnection` | 读取游戏进程改为走 `SimulantCompatibility.GetStandaloneGameProcess`（仅校验 `simulant` 自身的 `NativeGameMemory` 权限，不再要求 PostNamazu 权限）；`UpdateGameProcess` 中的 `_subscribedXivPlugin` 改为读取 `XivPluginInterop.plugin`；未授权时点击重试连接写入提示日志 |
| `GameInterop.Connect` | 入口加入 `SimulantCompatibility.EnsureStandaloneNativeAccess` 权限校验，未授权时禁止附加游戏进程 |
| `FirewallService.SendHookEnableCore` / `Enable` | 沿用 v0.1.0.0 的三处短跳与就近代码洞补丁及前置校验，但内存读取与进程句柄改为走 Simulant 自身的 `GameInterop.Connection.Memory` 与独立权限门禁，不再依赖 PostNamazu |
| `XivNetworkLog.WriteLine` | 上游通过反射读取 `FFXIV_ACT_Plugin._iocContainer` 写入模拟战斗日志；改为按 `FFXIV_ACT_Plugin` 的 `ParseMediator.BeforeLogLineRead` 格式（`[HH:mm:ss.fff] <LogMessageType> <HexId>:<payload>`）直接派发到 Host 的 `FormActMain.ParseRawLogLine`，供同 Host 内的 Triggernometry 触发，同时不污染真实 `Network_*.log` |
| `PluginUpdater.RunAsync` / `PluginHost` 配置路径 / `ZoneService.TryEnterTerritory` | 沿用 v0.1.0.0 的做法：禁用自更新、配置目录重定向到 ACT 数据目录、保留区域加载追踪日志 |

### 已知情况与验证记录（v0.4.0.0）

- 权限仅需授予 Simulant 的「原生游戏内存」，不再要求 PostNamazu 或 Triggernometry。
- Host 在仅启用 Simulant（未启用 Triggernometry）时也会挂载空的 `BeforeLogLineRead` 订阅，确保 `/e sim ...` 默语命令能通过 `OnLogLineRead` 到达 `SimCommandListener`。
- 2026-11-27 本机游戏内实测（Debug 构建加载 `Simulant.dll` v0.4.0.0）：成功完成弹窗授权、预加载 `SimuMagic` ACL 垫片、保存配置至 DACT 的 `Config\Simulant.config.xml`、加载区域 1122 / 1238 并运行预设模拟（如 U7a P13 / P22），期间无 Simulant 报错或闪退。
- `GenerateActLogs`（实验性模拟 ACT 日志）在离线冒烟中已验证按 `ParseMediator` 格式派发至 `FormActMain.OnLogLineRead`，尚未在游戏内实测开启该选项后的触发器联动。

## 维护者验证

常规 Package / Host smoke 覆盖手动导入前无安装记录、专用分类、默认权限、全部 8 种权限组合与签名检查。
使用维护者自行提供的固定原版 DLL 运行集成测试：

```powershell
$env:ACTCOMPAT_SIMULANT_DLL = '绝对路径\Simulant.dll'
& 'tests/DalamudActCompat.PackageSmokeTests/bin/Release/net10.0-windows/DalamudActCompat.PackageSmokeTests.exe'
& 'tests/DalamudActCompat.HostSmokeTests/bin/Release/net10.0-windows/DalamudActCompat.HostSmokeTests.exe' `
  --simulant $env:ACTCOMPAT_SIMULANT_DLL 'vendor/BundledActPlugins'
```

测试加载真实 PostNamazu、Triggernometry 和 Simulant，验证原版界面、CSV、依赖顺序、缺失授权/游戏进程时的错误、Triggernometry 回调、ACT 日志、实体地址与正常卸载。CI 中下载的固定 DLL 仅作为临时测试输入，不进入产品包。
启动条件回归覆盖正确区域、未加载、加载中、地图不匹配和防火墙关闭；原版点击处理函数在未初始化时不会创建会话。原生回归仅在测试进程内分配合成函数，复现 FF2425 Hook 返回到入口 +10 的情况，不修改游戏进程。游戏内初始化、防火墙开启/关闭恢复、地图切换及 P6 宇宙天箭已验证；不据此宣称所有区域和预设均已验收。
