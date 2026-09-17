# 绝伊甸、绝妖星绘图诊断

Host 对 Triggernometry 2.2 的原始执行流程增加被动观察，不修改订阅 XML、开关、
变量、条件或动作顺序，也不增加等待或重试。原有 U7b 初始化时序补丁保留。

启动时 `DACT_DRAW_DIAG_READY` 表示这些观察器已装入。详细记录以
`DACT_DRAW_DIAG ` 加单行 JSON 输出，由现有 Host 日志通道写入 `dalamud.log`。
仅在地图 1238 / 1363 且有 ReadCombatLogs 权限时收集。节点按祖先名称
U7a / U7b 识别；重命名了整组前缀的自定义版本不在本轮节点观察范围内。
旧 TN ABI 保持原流程，启动记录 `DACT_DRAW_DIAG_UNAVAILABLE`。

| stage | 可以确认的事情 |
|---|---|
| received / queued | Host 收到的原始日志、进入 TN 队列的日志；通过时间与 actor Key 关联转换前后 |
| inventory / node-config | TN/Host 程序集版本、注册回调数、相关节点是否启用、父级是否启用、来源与订阅权限限制 |
| match | 原正则确实匹配，并附当时的重触发冷却状态 |
| folder-filter | 原目录过滤函数的结果，没有额外重新计算条件 |
| condition-blocked / fire-return | 原节点条件是否阻止执行，以及动作是否接受排队/延后执行 |
| action-enter / action-return | 原动作进入和返回、动作序号、原动作结果与前后关键变量 |
| callback-enter / callback-return / callback-throw | 实际回调名、注册数、解析后的数值参数、返回或异常 |
| entity-change | 最近相关 actor 从缺失到就绪、地址/BNpcID 变化或消失 |
| exception / observer-error | 原节点/动作异常；诊断观察器自身异常（不会阻断原动作） |

`event.Key` 用原日志时间和 actor 关联；`event.DigestValue` 区分同一时间的不同事件。
每次上下文有 `contextId`，每次调用有 `callId`，可串联延迟动作。
状态包括 Host 实体快照的存在性、地址、BNpcID、快照年龄，游戏进程是否已登记，
原生/命令权限，以及 U7a 光轮、U7b P1 冰火散摊、myIdx 等限定变量和光轮开关。
快照状态属于 Host 已收到的数据，不等于额外读取了游戏内存；不主动初始化鲶鱼精，
也不额外扫描内存。连接或扫描失败仍结合原鲶鱼精启动日志及回调异常判断。

`registered=0` 表示未注册的静默调用；`-1` 表示暂时无法读取注册表。
`callback-return` 只表示处理函数返回，不能证明游戏已渲染；动作返回也不能覆盖同一
上下文的异常记录。`acceptedOrDeferred=true` 可包含等待互斥锁，不表示绘图已经完成。
变量 null 表示该项缺失；`unavailable-or-busy` 表示无法在不阻塞原线程的前提下采样。

每分钟分别限额：120 条接收/入队事件、600 条节点/回调/状态记录、60 条错误记录。
原始事件洪流不会耗尽节点或错误预算。下一窗口首条记录携带
`suppressedSincePreviousWindow`；预算耗尽期间不能用“没有日志”证明“没有触发”。
节点清单最多每分钟一次、最多 48 个相关节点；实体跟踪最多 64 个、保留 30 秒。
NPC 生成/读条过滤玩家施法噪声，仍保留玩家点名和状态事件。无完整聊天、原始日志文本
或任意绘图参数转储；原生地址/数值 CSV 记录原值，其他绘图参数只记长度与摘要。

## 取证方式

1. 使用诊断版启动游戏，检查 `DACT_DRAW_DIAG_READY`，保留同次启动的初始化日志。
2. 进入对应副本正常触发机制。缺圈时记录大概时间、阶段和缺失效果。
3. 保留该次 `dalamud.log`（若轮转则一并保留相关旧文件），对照上述 stage 查断点。

这份补丁增强取证，不宣称修复绝伊甸缺圈。仅有输出正常的正例不能反推实战前置正常。

## 回归

- 基础测试覆盖事件摘要、隐去名字/任意文本、预算与抑制计数、日志权限、其他地图无输出、
  未注册回调、原异常传播，以及观察器失效时仍执行原回调。
- 对真实重写 DLL 的七处入口执行 JIT 检查，并验证原正则返回值不变。
- `--probe-drawing-diagnostics Host.exe <plugin-root> <独立配置目录>`：原 U7a 光轮读条/输出
  两节点，回调在隔离 Host 内替换为记录器；验证原读条产生火安全变量、正常延迟输出、
  功能关闭、缺实体/晚到和故意抛错。没有游戏内存权限，未连接或控制实际游戏。
- 现有 `--probe-triggernometry-u7b-ordering` 验证原分散 8 圈/分摊 2 圈加箭头、半场刀和
  晚到实体时序保持，同时检查实体到达和原回调诊断，无 observer-error。

`Fixtures/U7aOrb.xml` 来自 U7a v0.11.1 本机订阅缓存的两个原节点及祖先配置。
完整来源 SHA-256：`A60984AA5491D1ABB044A978C7CB30A0EB3A7E7DFEDD72F68A68274C1A54350B`。
来源：<https://1824544011.cdn.123clouddisk.com/1824544011/Remote_Triggers/U7a.xml>。
测试命令会写入独立配置目录，不能指向正式用户配置。
