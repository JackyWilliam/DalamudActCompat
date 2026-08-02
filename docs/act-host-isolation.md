# 独立 ACT Host：实现与安全边界

更新日期：2026-07-31

## 结论

第二阶段采用混合架构。Triggernometry、PostNamazu、FoxTTS 和基于 manifest
发现的传统 ACT 插件由 `DalamudActCompat.Host.exe` 加载；FFXIV 进程只保留
Dalamud 数据采集、官方解析/Overlay 核心、ImGui 状态界面、IPC 客户端和经过
验证的语义命令 Broker。

这解决的是现实中的故障域问题：传统插件死循环、WinForms 死锁、阻塞调用、
未处理托管异常、Host 原生崩溃和 Host 内存失控不再与 FFXIV 共用进程。单纯把
插件换到另一个线程不能提供这些保证，因为 .NET 不能安全终止任意失控线程，
`Thread.Abort` 也不能恢复被破坏的锁/原生状态。

## 进程与线程模型

```text
FFXIV / Dalamud
├─ 游戏数据与官方解析/Overlay 核心
├─ ImGui 状态/权限界面
├─ ActHostSupervisor + HostIpcClient
└─ 白名单语义命令 Broker
          │ current-user-only named pipes
          ▼
DalamudActCompat.Host.exe
├─ ACT/NotACT STA 消息循环
├─ 每个传统插件的 STA WinForms 消息循环
├─ Triggernometry / PostNamazu / FoxTTS
├─ 有界 Clipboard STA 工作线程
└─ IPC reader / writer / heartbeat
```

游戏 Framework Update 只发布非阻塞事件，不调用传统插件。日志先进入有界
游戏侧队列，再按最多 128 条或 64 KiB 批量发送。Host reader 调用 ACT 回调；
如果回调卡住，独立 heartbeat 仍会报告“已接收序列未前进”，游戏侧看门狗在
5 秒后判定 Host 处理停滞并终止/重启 Host。

## IPC 协议 v2

传输使用两条仅当前用户可访问的命名管道，分别承载 game-to-host 与
host-to-game。帧为 4 字节长度前缀加 JSON，单帧最大 1 MiB。公共信封包含：

- 协议版本、随机 session ID、单调 sequence；
- 类型、优先级、创建时间和可选 deadline；
- correlation ID 与类型化 payload。

游戏到 Host 支持握手、权限快照、日志批、区域、战斗开始/结束、打开插件 UI、
关闭和仅测试构建启用的故障注入。Host 到游戏支持握手、心跳、健康/阶段状态、
结构化诊断、命令请求、命令结果和关闭确认。未知 session、倒退 sequence、过期
deadline、超长帧和未知命令均拒绝。

协议不传递函数地址、内存地址、任意程序集、任意命令行、PowerShell 或脚本。

## 队列与背压

| 队列 | 容量 | 满载策略 | 保证 |
| --- | ---: | --- | --- |
| IPC 控制/关键 | 256 | 拒绝新项并记录；关键区域/战斗状态保留最新副本供重连重放 | 单连接内保序，不声称断电式持久可靠 |
| IPC 数据 | 8192 | 丢弃最旧低优先级项，累计 drop 计数 | 保留剩余日志顺序 |
| 游戏侧待发送日志 | 8192 | 丢弃最旧日志 | 每批 128 条/64 KiB，20 ms flush |
| NotACT 日志 | 8192 | 丢弃最旧并按 2 的幂次限流告警 | 有界内存 |
| NotACT combat action | 4096 | 丢弃最旧并计数 | 有界内存 |
| Triggernometry event | 8192 | 丢弃最旧并限流告警 | 通过 Adapter 修补上游无界队列 |
| Clipboard | 8 | 拒绝新请求 | 最多 200,000 项、8 MiB 文本 |
| 游戏侧命令 Broker | 64 | 拒绝并返回 `busy` | 单消费者，禁止按请求无限 `Task.Run` |

状态型消息只保留最新值。控制事件不与高频日志共用数据容量；断线后只重放最新
区域和战斗状态，旧日志不无限缓存。

## 看门狗与熔断

- heartbeat 每秒一次；3 秒无心跳进入 suspect，5 秒无心跳判死；
- heartbeat 存活但 Host 的 last-received sequence 5 秒不前进，同样判死；
- working set 超过 1.5 GiB 或线程超过 256，判为 Host 故障；
- 自动重启使用 1、2、5 秒退避；10 分钟内超过三次失败打开总熔断，等待手动恢复；
- 手动停止会取消已计划的自动重启，手动重启会清除失败历史；
- ACT 回调记录完成次数、异常、慢调用、当前执行项和持续时间；连续 5 次异常或
  连续 3 次超过 2 秒时打开回调熔断；
- 诊断按错误指纹保留累计次数，只发前 3 次和后续 2 的幂次，防止刷屏；
- Host 终止只影响兼容 Host，FFXIV 与 Dalamud 不退出。

进程内回调熔断只能阻止“下一次”调用，不能抢占已经死循环的线程；抢占由游戏侧
终止整个 Host 进程完成。若将来要求某一个恶意插件不能拖死同 Host 的其它插件，
必须继续拆为每插件/信任域一个 Worker 进程。

## 权限与命令边界

权限按插件和能力保存，可在设置中撤销：日志读取、本地配置、TTS、Clipboard、
网络、启动外部程序、文件写入、游戏命令、原生游戏内存和高风险脚本。默认只允许
日志、本地配置、TTS 与 Clipboard；网络、进程、写入、游戏命令、原生内存和脚本
默认拒绝。授权判断和允许/拒绝的 Broker 调用都会写审计日志。

Triggernometry 的 TTS 请求先由游戏侧按插件身份、能力和 2 秒 deadline 授权，实际
FoxTTS 回调只在独立 Host 内执行。最多保留 64 个待授权请求；无输出提供者、过期、
拒绝或队列满都会显式失败。FoxTTS 卡死会阻塞 Host 的处理进度并触发进程级恢复，
不会把第三方音频回调带回 FFXIV 进程。

当前游戏侧只接受：

- `triggernometry` 的有界文本 `tts`；
- `postnamazu` 的单条、长度受限、无换行、斜杠开头的语义聊天命令。

PostNamazu 的任意地址 read/write/`calli` 和旧 GreyMagic 入口在外部 Host 中明确
抛出“不支持”，不会降级为假成功。即便用户授权游戏命令，参数仍需再次验证，并
有 2 秒 deadline/cancellation；超时后的 Framework 回调会再次检查取消状态，避免
迟到副作用。

限制：同一 Host 内仍是完全信任的 .NET 代码。恶意插件可以绕过托管 Broker 直接
访问桌面 API，也可能伪造另一个插件的逻辑身份。严格来源隔离需要独立受限 Worker、
受限令牌或 AppContainer；现阶段 UI 明确显示这一点，不把能力开关宣传为 OS 沙箱。

## Triggernometry Adapter

- 管理员检测保留真实结果；普通权限时改为说明可用的日志、区域、战斗、正则、
  配置、Clipboard 和 Broker TTS 能力，不伪造管理员身份；
- 高风险脚本、网络仓库更新与外部进程启动分别经过权限检查；
- `Thread.Abort` 被替换为结构化“线程未停止”报告，由 supervisor 结束 Host；
- 上游两个无界事件 enqueue 改为 8192 项 drop-oldest；
- 原版 `DataSubscription.ZoneChanged` 在 event-only facade 中不存在，专属 Adapter
  注册/注销区域监听，传递真实 territory ID/zone name 并更新区域限制；
- 内部 Error 日志每秒采样，按 timestamp/message 去重并输出结构化诊断。上游只保留
  message 时，stack 明确标注“上游未保存”，不伪造堆栈。

## PostNamazu Adapter 与阶段状态

| 阶段 | 当前状态 | 说明 |
| --- | --- | --- |
| 程序集加载 | 成功/失败实时显示 | 验证真实 DLL 路径 |
| `InitPlugin` | 成功 | 外部 STA WinForms Host 已加载真实插件 |
| ACT Host / Host IPC | 成功 | NotACT 与 v2 命名管道均已初始化 |
| FFXIV_ACT_Plugin 发现 | 成功 | 提供官方类型身份的 event-only facade |
| Triggernometry 发现 | 成功或失败实时显示 | 在 PostNamazu 初始化前加入 ACT 插件列表 |
| OverlayPlugin 发现 | 未实现 | Overlay 留在游戏侧，不把完整 DLL 暴露给不可信 Host |
| 游戏进程识别 | 未实现（安全限制） | 不暴露 raw `Process`、内存或注入入口 |
| 命令桥 | 成功 | 只提供白名单语义 Broker；旧 native 调用明确失败 |
| 日志系统 | 成功 | 走有界 ACT 日志批 |
| 命令发送测试 | 未测试 | 必须在游戏运行且用户明确授权后逐动作验证 |
| 卸载测试 | Host 退出时记录 | `DeInitPlugin` 有界等待；卡住则终止 Host |

“复制日志”的旧阻塞点是 PostNamazu UI 线程上的日志遍历、字符串拼接和最多 10 次
Clipboard 重试。Adapter 现在只在 UI 线程快照最多 200,000 项；字符串构建与最多
5 次、总等待有界的 Clipboard 操作在容量 8 的专用 STA 队列执行。失败不会永久锁住
PostNamazu UI，更不会阻塞 FFXIV。

## 已自动验证

- Host 握手、版本/session/sequence、任意命令拒绝和正常关闭；
- Host 直接 kill、客户端断管、Host reader 阻塞 30 秒时游戏侧测试继续前进；
- 控制队列溢出拒绝、数据队列 drop-oldest、状态压缩；
- NotACT 日志/combat action 队列上限；
- 连续异常打开单回调熔断；
- 真实 Triggernometry DLL 初始化与配置读取；
- 标准日志与 FFXIV 网络等价事件分别正则命中，完成游戏侧 TTS 授权并调度到
  Host 测试输出提供者；
- 区域变化更新区域限制后命中；
- 战斗开始/结束事件分别命中；
- Triggernometry、PostNamazu、FoxTTS 外部加载和 Host 正常退出。

仍需游戏运行后验证：重复打开/关闭真实 UI、实际 TTS 音频、PostNamazu 每一条允许
命令、Host 被任务管理器结束后的 UI 恢复、退出游戏后的孤儿进程清理，以及真实战斗
高峰下的积压/内存曲线。
