# ACT Compatibility Layer 第二阶段调查报告

调查日期：2026-07-31

调查基线：

- DalamudActCompat：`main` 分支，开始调查时工作树干净，线上基线 0.2.33。
- Triggernometry 中文维护版：2.1.1.2；上游源码 `MnFeN/Triggernometry`，调查提交 `2ada750fb78dbce27cb54e9a0b60e89c8aabf33c`。
- PostNamazu：1.3.6.6；上游源码 `Natsukage/PostNamazu`。
- IINACTEx：`Latihas/IINACTEx` 的 `cn` 分支，调查提交 `b0985055bfa1cc58f1e0d4d6d1eabd2d1c8e35b5`；Triggernometry 子模块 `ca19636f125de4a8141ad18f5fd295b1c37ef7c9`，PostNamazu 子模块 `4eedf8e512bf70cec65eb98d9275be8548991d05`。
- 运行证据：`2026-07-30 23:37:56.335`，Dalamud 记录 `Long UiBuilder(Dalamud ACT Compat) detected, 11269.0566ms > 100ms`。

## 1. Triggernometry 管理员提示

管理员检测位于：

`Source/Triggernometry/Core/RealPlugin/RealPlugin.Helper.cs` 的
`RealPlugin.CheckIfAdministrator(bool warnIfNotAdmin)`。

实现调用 `WindowsIdentity.GetCurrent()` 与
`WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)`。检测结果还保存在
`isRunningAsAdmin`，并被 `Core/Scripting/ScriptSecurity.cs` 使用：进程已提升时，
高风险脚本 API 需要额外的 `AllowAdmin` 授权。把结果伪造成 `true` 不仅不真实，
还会破坏 Triggernometry 自己的脚本安全模型。

提示源自传统 ACT 部署假设，而不是 Triggernometry 基础触发功能的硬依赖。
传统 ACT/FFXIV_ACT_Plugin 可能因网络抓包驱动、跨进程读取游戏、控制更高完整性
外部进程等原因遇到权限限制。DalamudActCompat 已处于游戏进程内，并通过
Dalamud/FFXIV_ACT_Plugin 桥提供日志、网络等价事件、区域、战斗与实体信息，
这些能力不需要提升 FFXIV。

普通权限下可以支持：

- ACT 标准日志和 FFXIV 网络等价日志；
- 区域与战斗生命周期；
- 配置、正则、变量和本地触发器；
- 通过宿主能力提供的 TTS、声音与受控 Clipboard；
- 经用户授权、带超时的普通网络访问和普通用户目录文件访问。

普通权限下不能保证：

- 控制管理员身份运行的外部程序；
- 访问受 ACL 或完整性级别保护的文件、注册表和进程；
- 依赖管理员驱动或提升权限抓包器的功能。

推荐的 Triggernometry Adapter 保留真实的非管理员结果，但把旧的笼统 Toast
替换为宿主能力说明：

> 当前以普通权限运行。Dalamud 日志、网络事件、区域、战斗和 TTS 桥可用；
> 需要跨完整性访问受保护外部进程的动作不可用。请勿提升 FFXIV、Dalamud
> 或全部插件的权限。

## 2. Triggernometry 的 13 条 Error

实际 `Triggernometry.config.xml` 中恰好有 13 个启用、启动时更新的远程仓库。
它们都指向 `1824544011.v.123pan.cn`，所有 `UpdateLastChecked` 值集中在
`2026-07-30 23:37:58.075` 至 `.086`，而
`Config/TriggernometryRepoBackups` 不存在。调查时对 13 个地址并发执行 HEAD，
全部未在限定时间内返回。

`RealPlugin.UpdateRepositoriesAsync` 会并发调用所有仓库的
`Repository.CheckAndUpdateAsync`。远端元数据失败且本地备份不存在时，
`Repository.UpdateAsync` 再下载仓库；每个下载超时分别写入一条 Error。

| # | 仓库 | 地址尾部 | 类型 | 线程 | 重复性 | 分类 |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 问题自检工具箱 | `SelfTest.xml` | `TimeoutException` | 线程池 | 每次启动/更新周期 | 远程配置源 |
| 2 | 运行支持库 | `Utils.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |
| 3 | 7.0 M1-4 | `S7a.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |
| 4 | 7.2 M5-8 | `S7b.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |
| 5 | 7.4 M9-12 | `S7c.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |
| 6 | 7.X 极神 | `Ex7.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |
| 7 | 临时推送 | `temp.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |
| 8 | 6.3 绝欧米茄 | `U6b.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |
| 9 | 7.1 绝伊甸 | `U7a.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |
| 10 | 7.5 绝妖星 | `U7b.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |
| 11 | 特殊场景探索 | `field.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |
| 12 | 深宫 | `dungeon.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |
| 13 | 异闻迷宫 | `vc.xml` | `TimeoutException` | 线程池 | 同上 | 远程配置源 |

共同异常链：

`HttpClient.SendAsync` 的 `TaskCanceledException`
→ `HttpHelper` 转换为 `TimeoutException`
→ `Repository.UpdateAsync`
→ `RealPlugin.FilteredAddToLog(Error, ...)`。

13 个任务并发，因此具体哪个仓库先完成并不稳定。第一根异常是共同远端请求
超时，13 条记录只有一个独立根因，不是 13 个 ACT API 缺口。

当前 Triggernometry 的 `Repository.UpdateAsync` catch 只把 `ex.Message` 交给
内部日志，异常类型和完整堆栈已经在插件内部丢失；DalamudActCompat 也没有读取
Triggernometry 内部日志。因此旧运行无法恢复完整堆栈。兼容层必须先增加结构化
诊断采集，并明确标注“插件只提供消息”的记录，不能伪造堆栈。

## 3. Triggernometry 最小闭环差异

原插件预期：

- `ActGlobals.oFormActMain` 是完整 ACT 主窗体和调度对象；
- `ActPlugins` 可发现 FFXIV_ACT_Plugin、OverlayPlugin 和其它插件；
- 标准日志、网络日志、区域、战斗事件按 ACT 顺序到达；
- WinForms 有真实 STA 消息循环和 `ISynchronizeInvoke` 语义；
- 卸载时事件、动作、光环、端点和脚本线程可结束。

当前实现：

- 已提供插件发现、标准日志、战斗事件和 FFXIV DataRepository/DataSubscription；
- 每个传统插件有独立 STA WinForms 线程；
- `FormActMain.Invoke/BeginInvoke` 却直接在调用者线程执行；
- Triggernometry 的事件和动作队列无界；
- Triggernometry 卸载失败后调用现代 .NET 不支持的 `Thread.Abort`；
- 尚无自动化的配置读取、日志、网络、区域、战斗、正则、TTS、重复开关 UI、
  资源释放端到端验证。

管理员提示和 13 个远程仓库 Error 不等于闭环失败。闭环必须由可观测探针逐项
验证。

## 4. PostNamazu 分阶段状态

| 阶段 | 当前状态 | 证据与原因 |
| --- | --- | --- |
| 程序集加载 | 成功 | UI 已能打开 |
| `InitPlugin` 调用 | 部分成功 | UI、进程管理器、动作模块已创建；现代 .NET/强名称 GreyMagic 加载点可能中断尾部生命周期 |
| ACT Host | 成功 | `ActGlobals.oFormActMain` 存在 |
| FFXIV_ACT_Plugin 发现 | 成功 | Native Bridge 能通过 ProcessManager 取得 FFXIV 进程 |
| OverlayPlugin 发现 | 成功 | 运行日志显示共享事件处理器已注册 |
| Triggernometry 发现 | 未验证 | 类型和插件列表入口存在，但没有成功回调计数 |
| 游戏进程识别 | 成功 | Native Bridge 启动依赖此步骤，运行日志已越过 |
| 全局签名扫描 | 成功 | `GetOffsets()` 返回 true |
| 模块签名扫描 | 表面成功 | 状态文案没有 unavailable module；未做真实动作测试 |
| 命令桥初始化 | 表面成功 | IL 已重写到 Native Bridge；桥仍允许任意函数地址/内存访问 |
| 日志系统 | 部分成功 | ListBox 和 Dalamud 日志入口存在；没有结构化、限流和阶段状态 |
| 文本命令测试 | 未实现 | “native bridge active” 不包含端到端命令确认 |
| 标点/场地标点/预设 | 未验证 | 仍依赖通用 `calli/read/write` 等价假设 |
| 卸载测试 | 未实现 | WinForms Join 超时后可能遗留线程 |

`FFXIV entrance function found(7.3_raw)` 只表示旧 GreyMagic 入口签名被找到，
不表示命令发送、模块调用或 IPC 已连通。

## 5. PostNamazu 复制日志卡死

准确调用链：

`CmdCopyProblematic_Click`
→ `CopyLog(true)`
→ 枚举 `lstMessages.Items`
→ `StringBuilder.AppendLine`
→ 被 IL 改写的 `LegacyResourceCompatibility.SetClipboardText`
→ `RetryClipboard`
→ `Clipboard.SetText`。

调用发生在 PostNamazu 的 STA UI 线程，线程 apartment 本身正确。不存在异步
任务同步等待、Dispatcher、IPC 或插件调用。发生 `ExternalException` 时，
当前兼容补丁在 UI 线程 `Thread.Sleep(20 * (attempt + 1))`，前九次累计阻塞
900ms，第十次仍失败则重抛。日志遍历和字符串拼接也在 UI 线程，日志量大时会
进一步阻塞。

修复应快速快照 UI 项目，把字符串整理放到后台，再交给专用 STA Clipboard
服务；队列有界、请求可取消、整体有 deadline，失败后返回结构化状态。不能在
UI 线程重试或仅在外围加 try/catch。

## 6. 当前线程与死锁面

- Dalamud Framework Update：玩家/队伍/战斗状态采集、遭遇快照、当前所有
  PostNamazu 原生调用。
- PluginLifecycle 线程池任务：同步执行 `StartParser`、Overlay 和传统插件加载。
- 每插件 STA 线程：WinForms 配置 UI、`InitPlugin`、消息循环、`DeInitPlugin`。
- NotACT：日志读、日志写、AfterCombatAction 等线程，部分使用无界
  `ConcurrentQueue`。
- Triggernometry：无界 `Queue<LogEvent>`、动作线程、端点、脚本、光环和绘制
  线程。
- PostNamazu：原版进程监控、HTTP 与 GreyMagic 调度线程；部分已被补丁跳过。

高风险同步点：

- `NativePostNamazuBridge.RunOnFrameworkThread(...).GetAwaiter().GetResult()`；
- Framework Update 中执行任意地址 `calli/read/write`；
- `LoadedActPlugin.Load` 等待插件初始化最多 30 秒；
- 卸载等待插件 STA 线程 10 秒，超时后继续并留下线程；
- Triggernometry 线程 Join 失败后调用 `Thread.Abort`；
- NotACT 的假 `Invoke/BeginInvoke` 不执行真实线程切换；
- 无界事件/日志队列；
- 插件构造和 Dispose 中的同步异步等待。

## 7. 隔离方案评估

### 进程内线程隔离

能解决：

- 正常慢回调不再直接占用 Framework Update；
- 通过有界队列限制积压和内存；
- 对仍能返回的超时、异常做计数、暂停和熔断；
- 保持 UI 和事件入口可操作。

不能解决：

- 无法安全强制停止失控托管线程；
- 死循环、全局锁、栈溢出、线程池耗尽仍共享进程；
- 原生访问冲突和 DLL 崩溃直接终止 FFXIV；
- 严重内存增长仍影响游戏。

### 独立辅助进程

Host 进程负责 ACT API 模拟、WinForms、Triggernometry、PostNamazu 非游戏内
部分、日志/触发计算和插件生命周期。FFXIV 进程只负责采集、状态 UI、监督、
IPC 和严格验证的最小命令 Broker。

Host 死循环、死锁、原生崩溃或内存失控时，游戏侧可结束并重启 Host。单个
插件卡死无法在同一 Host 内安全卸载；看门狗要记录最后执行插件，重启 Host 后
隔离该插件。未来可按兼容组拆分 Worker 进程。

### 推荐

采用混合架构，并分阶段迁移。线程隔离是迁移期减灾，不作为最终安全边界。

## 8. IPC 协议草案

传输：当前用户 ACL 限制的双向命名管道；长度前缀帧，不使用无限 JSON 行流。

公共帧头：

- `ProtocolVersion`
- `SessionId`
- `Sequence`
- `MessageType`
- `Priority`
- `CorrelationId`
- `TimestampUtc`
- `DeadlineUtc`
- `PayloadLength`

游戏到 Host：

- `Hello`
- `LogLine`
- `NetworkLogLine`
- `ZoneChanged`
- `CombatStarted`
- `CombatEnded`
- `StateSnapshot`
- `Shutdown`

Host 到游戏：

- `HelloAck`
- `Heartbeat`
- `Ack`
- `TtsRequest`
- `PostNamazuCommand`
- `PermissionRequest`
- `HealthReport`

未知版本、未知消息、过期 deadline、越界长度和未授权命令直接拒绝。禁止传输
函数地址、内存地址、任意代码或 PowerShell。

## 9. 队列与背压

- 控制队列容量 256：区域、战斗开始/结束、关闭、权限响应；必须保序、ACK、
  有限重试。
- 数据队列容量 8192：标准日志和网络日志；保序，满时丢弃最旧低优先级项，
  记录连续区间和丢弃计数，不允许无限积压。
- 状态槽：玩家、队伍、战斗状态按 key 只保留最新值。
- 每批最多 128 条或 64 KiB，最长聚合 20ms。
- 关键控制事件使用独立容量，不能被战斗日志挤占。
- 断线期间只保留有限关键重放窗口，不能宣称绝对可靠。

## 10. 看门狗与熔断

- Host 心跳：1 秒；
- 3 秒无心跳：Suspect，停止发送低优先级数据；
- 5 秒无心跳：结束 Host，游戏保持运行；
- 重启退避：1、2、5、15、60 秒；
- 10 分钟内三次失败：进入手动恢复；
- 单插件回调超过 100ms：警告；
- 连续三次超过 2 秒：暂停新事件并标记熔断候选；
- 队列 75%：警告；90%：丢弃低优先级旧事件；
- 监测异常/超时次数、IPC 延迟、内存及增长率、线程数量、UI 无响应和日志速率；
- 日志按错误指纹限流并保留累计次数。

分级处置：

1. 记录一次结构化警告；
2. 暂停对应插件的新事件；
3. 丢弃其低优先级积压；
4. 熔断并持久化隔离原因；
5. 尝试正常卸载；
6. 无响应时重启 Host；
7. FFXIV 和 Dalamud 不退出。

## 11. 权限边界

权限类别：

- 只读日志；
- 本地配置读取；
- TTS；
- Clipboard；
- 网络请求；
- 启动外部程序；
- 文件写入；
- 游戏内语义命令；
- 高风险脚本。

只读日志和插件自身配置目录读取可默认允许。TTS/Clipboard 需可见状态。
网络、外部程序、任意路径写入、游戏命令和脚本默认拒绝或首次明确授权，按插件
和动作类别持久化，可撤销并写审计日志。

同一完全信任 .NET Host 内的插件仍能绕过宿主 API 直接调用文件、网络和
`Process.Start`。Broker 是必要边界但不是完整 OS 沙箱；强制执行需要受限令牌、
AppContainer 或独立 Worker。任何插件都不得自动提升权限。

特别风险：当前 Triggernometry 的 13 个远程仓库全部具有
`AllowScriptExecution=true` 和 `AllowProcessLaunch=true`。迁移到新 Host 时
不得静默继承为宿主授权。

## 12. IINACTEx 对照结论

IINACTEx 通过项目引用直接编译大幅修改的 Triggernometry/PostNamazu fork，
把它们深度绑定到 Dalamud 服务和 ImGui；它还在多处使用
`RunOnTick(...).Wait()`/`.Result`，并直接关闭管理员 Toast。该实现说明了插件
需要哪些数据和发现入口，但不满足本项目的独立进程、安全命令、上游兼容和
故障隔离目标。

可借鉴的是：

- 明确维护 FFXIV_ACT_Plugin、OverlayPlugin、Triggernometry、PostNamazu 的
  插件发现顺序；
- 为 Triggernometry 提供游戏实体、区域、脚本环境等明确能力；
- 为 PostNamazu 提供语义化的宿主入口。

不可照搬的是：

- 把第三方源码整体编入 Dalamud 插件；
- 注释掉管理员提示；
- 在 Framework Update 同步执行动作；
- 继续维护大规模插件 fork；
- 用任意内存/原生函数作为桥接协议。

## 13. 迁移与测试顺序

1. 落地结构化诊断和阶段状态；
2. 修复 Clipboard 非阻塞路径；
3. 建立有界队列、统计与迁移期熔断；
4. 定义共享 IPC 协议和 HostSupervisor；
5. 把 Triggernometry 移入 Host，验证初始化、重复 UI、配置、标准/网络日志、
   区域、战斗、正则、文本/TTS 和卸载；
6. 把 PostNamazu UI/计算移入 Host，游戏侧只保留逐动作白名单 Broker；
7. 实现命令测试和卸载测试后才报告 Ready；
8. 默认关闭旧进程内传统插件路径；
9. 执行死循环、30 秒阻塞、IPC 永不返回、十万行复制、Host 崩溃/强杀、
   断管、队列满、持续异常、内存增长、退出清理、重连和熔断测试。

验收硬条件：任何 Host 或传统 ACT 插件故障都不能让 Framework Update、
Dalamud UI 或 FFXIV 发生永久同步等待。

## 14. 实施结果（2026-07-31）

调查后的实现采用上述混合架构，详见
[`act-host-isolation.md`](act-host-isolation.md)。传统 ACT 插件已从 FFXIV 进程移入
独立 Host；IPC v2、有界队列、心跳/处理进度看门狗、自动重启/熔断、权限快照、
语义命令 Broker、PostNamazu 阶段状态和结构化异常记录已经落地。

真实 Triggernometry 离线闭环首次运行时没有触发任何规则。第一根兼容层异常不是
正则或 TTS，而是 `FormActMain.CurrentZone` 初始为 `null`：Triggernometry 收到第一条
日志后执行 `ZoneChanged(null)`，而 `PassesZoneRestriction(null)` 对所有触发器返回
`false`，导致所有规则被标记为 `ZoneBlocked`。将 ACT Host 的初始区域改为真实的
“未知但非 null”空字符串后，标准日志和网络等价事件闭环通过。

继续验证战斗事件时，`OnCombatStart` 已到达但 `OnCombatEnd` 未到达。根因是 NotACT
`SetEncounter` 创建 encounter 后没有设置 `Active`/`StartTimes`，`EndCombat` 因此
提前返回。这是通用 ACT 生命周期缺口；修复状态机后，战斗开始和结束均通过真实
Triggernometry ACT-source 触发器验证。

区域事件另有一个明确差异：Triggernometry 原版通过
`FFXIV_ACT_Plugin.DataSubscription.ZoneChanged` 反射订阅，而外部 Host 仅提供 event-only
facade，不初始化原版抓包/内存 IOC。专属 Adapter 现在注册/注销区域监听，传递真实
territory ID 与 zone name；带区域限制的真实触发器已验证通过。该 Adapter 不提供
进程、内存或抓包假对象。

离线自动化当前已通过配置读取、标准日志、网络等价事件、区域限制、战斗开始、
战斗结束、正则、TTS command request、正常卸载，以及 Host kill/断管/30 秒阻塞、
有界队列和回调熔断测试。游戏未启动不影响这些合成事件测试；实际 TTS 音频、
PostNamazu 游戏命令和 Dalamud UI/退出行为仍明确标为待游戏内验收，不能报告为成功。
