# 7.57 网络兼容修复（0.4.5.12）

用户在国服更新后遇到 `Parser initialization failed`，缺失签名为
`E8 ?? ?? ?? ?? 4C 8B 4F 10 8B 47 1C 45`。0.4.5.11 仍使用 7.56h
签名、操作码和解混淆资源；单独重启解析器不能替换这些编译进程序的数据。

## 来源和改动

| 环节 | 本次版本 | 固定依据 |
| --- | --- | --- |
| IINACT | 2.10.3.10，保留 DACT 修改 | [官方版本差异](https://github.com/marzent/IINACT/compare/v2.10.3.8...v2.10.3.10) |
| 接收签名 | 7.57 两个精确编译变体；旧客户端保留旧签名 | IINACT [9fc6047](https://github.com/marzent/IINACT/commit/9fc6047ee2f882186c7522f238d4a0f647484b56) 与 Unscrambler [d00b565](https://github.com/perchbirdd/unscrambler/commit/d00b565b26f42aa64fc733fb3645b5c7132a04e7)，均选择第三个接收调用，不接受不足三个匹配 |
| OverlayPlugin Core | 0.19.110，国服/国际服/韩服 7.57、繁中 7.25 表 | [正式版本](https://github.com/OverlayPlugin/OverlayPlugin/releases/tag/v0.19.110) |
| Unscrambler.XIV | 7.57.0 | [NuGet](https://www.nuget.org/packages/Unscrambler.XIV/7.57.0)，源码 d00b565；目标客户端 `2026.10.06.0000.0000`，19 项混淆操作码、83 × 4 = 332 字节密钥表 |
| Machina | 保留 DACT 分支，更新三地区各 26 项表 | [marzent/machina 3c8bc7d Global](https://github.com/marzent/machina/blob/3c8bc7d302801dc948edb307485c204ed6fce2af/Machina.FFXIV/Headers/Opcodes/Global.txt)，逐项核对 [FFXIVOpcodes be69b56](https://github.com/karashiiro/FFXIVOpcodes/blob/be69b5689e88f503b8665999edc16deb93d8e05e/opcodes.json) 的 Global/CN/KR 独立区域表 |
| 抹茶 | 保持 26.9.26.1753 DACT5，Host 重写其 28 项事件映射 | [Matcha 0a5fb86](https://github.com/thewakingsands/matcha/blob/0a5fb86c419a0526b37a00cb79d52732d2f283c8/Cafe.Matcha/Constant/MatchaOpcode.cs)，CN/Global 分别核对 |
| 银山雀儿 | 保持 0.6.0.4，更新 Host 归一化映射 | InitZone `02CC`、FateInfo `0259`、ActorControlSelf `01E6`；防止远端旧数据覆盖 |
| FFXIV ACT Plugin | 3.0.3.1 不变，刷新内嵌 IINACT 日志身份 | 官方 latest 与 IINACT updater 当时均为 3.0.3.1 |

IINACT 2.10.3.10 本身仍引用 Unscrambler 7.56.1；其 Machina 提交的 CN/KR
表也没有与 Global 一同更新，因此未机械复制整个上游分支。当前 CN/Global/KR
操作码一致的结论来自本次三份区域表比较，不能外推到未来补丁。

国际服使用官方 7.57 嵌入资源；国服只复用已核准的操作码，继续扫描本区
运行时密钥表并验证大小，不借用国际服固定 RVA。旧版解混淆资源及未知版本
告警行为保留。

本轮仅修复游戏更新所需的兼容面。Cactbot 正式包仍为 0.37.5；上游未发布
提交、抹茶新增钓鱼统计功能和 Triggernometry 的新数学函数没有纳入本次版本。

## 验证与边界

- 签名测试使用实际扫描器和合成机器码，验证两个 7.57 变体、旧客户端、第三
  通道选择、不足三个匹配失败与异常传播；不会挂钩或启动游戏。
- 包级回归验证官方解混淆资源、国服独立表地址、错误表大小与未知版本拒绝、
  国服/国际服合成 ActorCast/ActionEffect 完整解码和四坦克无敌技能 ID。
- Host 回归使用真实 Matcha DLL，核对两地区 FATE、排队、钓鱼生命周期及
  错误方向/过期编号拒绝；SilverDasher 验证真实 DLL 重写和两地区操作码归一化。
- Release 构建通过（0 错误、642 条依赖编译警告）；Package、Host、Scanner、
  LegacyResource、U7b 时序与绘图诊断回归全部通过。分包脚本核对了运行时版本、
  35 个 ACT 文件、5 个内置扩展哈希与资源清单。
- 本机 `D:/FFXIV` 在核查时仍为 `2026.09.15.0000.0000`，未取得新版国服或
  国际服实际游戏验收。离线回归不代表实际 FFLogs 排名校验通过。

正式发布从合并后的主线重新构建，并核对公开附件与原订阅源；游戏内自然验收
仍待反馈。发布状态以 [Release](https://github.com/JackyWilliam/DalamudActCompat/releases/tag/v0.4.5.12)
为准，正式发布不代表已覆盖本机安装。

## 0.4.5.13 发布前追加核对

上游在 2026-10-13 20:52 UTC 发布 [FFXIV ACT Plugin 3.0.3.3](https://github.com/ravahn/FFXIV_ACT_Plugin/releases/tag/3.0.3.3)，
明确补充 7.57 支持并保留繁中 7.25。0.4.5.13 首次正式流水线检测到
3.0.3.1 已过期，在创建发布前停止；本次同步更新解析器和 SDK，并调整包级版本守卫。

官方 ZIP SHA-256 为 `8254f003cb8018b7f6c66208400bd3191793d1c4a5cbd839e1331316a9351472`。
IINACT updater 的 ZIP 省略 Deucalion，归档本身不同；其中原始
`FFXIV_ACT_Plugin.dll` 与官方包逐字节一致，SHA-256 均为
`af1031cdba8eeba8f53dd9d618e58d62d4919bfa77db9b31c568bd04cb359093`。
继续使用已有 Costura 拆包和 IINACT 宿主补丁，实际日志头必须为
`This is IINACT 2.10.3.10 (API 1.6.0) based on FFXIV_ACT_Plugin 3.0.3.3`。
既有地区解析范围与绑定权限逻辑不变，实际游戏与 FFLogs 验收边界仍适用。

## 0.4.5.14 发布前追加核对

上游在 2026-10-20 22:59 UTC 发布 [FFXIV ACT Plugin 3.0.3.4](https://github.com/ravahn/FFXIV_ACT_Plugin/releases/tag/3.0.3.4)，
补充 7.57 热修支持并将 Deucalion 更新为 1.5.1。0.4.5.14 首次正式流水线检测到
3.0.3.3 已过期，在创建发布前停止；本次同步更新解析器和 SDK，并调整包级版本守卫。

官方 ZIP SHA-256 为 `089306e497dd4b8fb79290472f006b889fd21fc3d3816ce41ef9a0300feb2d11`，
与 GitHub 资产摘要一致。IINACT updater 的 ZIP 只含解析器、省略 Deucalion，其中原始
`FFXIV_ACT_Plugin.dll` 与官方包逐字节一致，SHA-256 均为
`f7f0c8894ba1a152b6a453427773496f5948cf412171da773578d8671f643a81`。
继续使用已有 Costura 拆包和 IINACT 宿主补丁，实际日志头必须为
`This is IINACT 2.10.3.10 (API 1.6.0) based on FFXIV_ACT_Plugin 3.0.3.4`。
IINACT 子模块提交 `c9f34f6`（分支 `feature/parser-3-0-3-4`）。既有地区解析范围、
绑定权限逻辑不变，实际游戏与 FFLogs 验收边界仍适用。

同一次正式流水线的随包扩展门禁同时发现 Triggernometry 已过期：上游 CN 维护版发布 2.2.1.0，
随包仍为 2.2.0.1。本次按 `tools/sync-bundled-act-plugins.ps1` 同步，仅 Triggernometry 变化，
新 SHA-256 为 `9a3c430c00a82ca99f83b2a373adbe1ba0c365a7f06bbf517bc6b76cbd5bb37c`，与扩展更新器
为用户安装的 2.2.1.0 逐字节一致；ACT.FoxTTS、PostNamazu、银山雀儿与抹茶保持不变。2.2.1.0 新增的
PictoACT 写法已在本版游戏侧适配，并按真实 2.2.1.0 解析器做了差分回归；Host 侧 Triggernometry
改写、U7b 顺序、事件时间与绘图诊断回归均改为在 2.2.1.0 上执行。

## 0.4.5.15 发布前追加核对（2026-10-27）

国际服在 2026-10-16 上线 7.57 热修（`2026.10.16.0000.0000`），全部操作码重排。上游
[IINACT 2.10.3.11](https://github.com/marzent/IINACT/releases/tag/v2.10.3.11) 同日跟进：
Unscrambler.XIV 7.57.1、OverlayPlugin 0.19.111（仅国际服 7.57h 操作码）、machina 国际服
7.57h 网络事件表与 ApplicableVersion。DACT 0.4.5.14 只同步了解析器 3.0.3.4，自有接收签名版本门仍只认
`2026.10.06`，国际服解析器启动即抛 `No supported zone receive signature`。

- IINACT `0036e08`（`feature/parser-757h`）：接收签名版本门加入 `2026.10.16`，沿用 7.57 两种精确签名；
  Unscrambler.XIV 7.57.1，锁文件 contentHash 与上游 2.10.3.11 一致，官方配置的密钥表
  `0x232A700`/932 字节与反馈用户国际服客户端运行时扫描结果一致；OverlayPlugin 国际服操作码改为 7.57h；
  IINACT 2.10.3.11，Logfile 身份重写为 `This is IINACT 2.10.3.11 (API 1.6.0) based on FFXIV_ACT_Plugin 3.0.3.4`。
- machina `20b1d04`：国际服 26 项操作码取自上游 marzent/machina `06d9ab4`，其中 19 项与
  [FFXIVOpcodes `d85eaeb`](https://github.com/karashiiro/FFXIVOpcodes/blob/d85eaebec24b08948550e09c005fe5860d17c3f2/opcodes.json)
  一致、2 项与 Unscrambler 7.57.1 一致，无冲突。国服、韩服仍为 7.57。
- 抹茶国际服 26 项映射改为 FFXIVOpcodes `d85eaeb` 的 7.57h 值，国服仍为 7.57；FateInfo 与 WorldVisitQueue
  在国际服 7.57h 尚无公开值，不再沿用 7.57 旧值。银山雀儿国际服 InitZone `0x009C`、ActorControlSelf
  `0x0101`，FateInfo 回到其自身占位 `0xF009`。

随包扩展门禁同时发现 Triggernometry 已过期：上游 CN 维护版发布 2.3.0.0（上游提交 `8b89333`），随包仍为 2.2.1.0。
按 `tools/sync-bundled-act-plugins.ps1` 同步后仅 Triggernometry 与其汉化变化，新 SHA-256 为
`f40d4a838442d2618fe07b29292bde7988a1d69aa36e797f5b9be49b4ec89f55`；ACT.FoxTTS、PostNamazu、银山雀儿与抹茶不变。

- 2.3.0.0 的 JSON 处理重构后，DACT 原先固定要求的 24 处 JavaScriptSerializer 调用变为 22 处，0.4.5.14 会拒绝加载
  2.3.0.0。改为要求至少一处、拒绝未知调用形态并证明替换后不再残留 System.Web 引用；2.2.1.0 与 2.3.0.0 均通过
  Host、Legacy Resource 与真实 TN 时间探针（`staleReplayed=False burstFires=2 stallFires=1 spawnSeen=True`）。
- PictoACT 对齐 2.3.0.0：`minuspolar` 取反时高度一并取反（上游 `1ddaa44`），十六进制 `E0000000` 视为实体引用
  （上游 `ef2c917`），多边形三角剖分移除重复、近重复（0.05）与共线顶点（上游 `3dc6f54`）。以真实 2.3.0.0 解析器
  反射核对，15 个在线仓库 2519 个样本与 24 个合成样本全部一致。
- 抹茶上游 thewakingsands/matcha 比随包源码 `d4fffc6` 多 19 个提交（钓鱼统计上传、Fishcake 导出、潜水艇部件等），
  不在发布门禁内，本版未同步。银山雀儿仍无可证明版本的公开清单。

实际国际服客户端尚未在本机验证（本机为国服），国际服解析、FF Logs 上传与抹茶、银山雀儿表现需反馈用户复测。

## 0.4.5.16 发布前核对（2026-10-30）

本版只包含触发器时间基准修复（PR #151），不改随包依赖。三项发布依赖门禁重新联网核对均通过：
FFXIV_ACT_Plugin 3.0.3.4（IINACT 2.10.3.11）、Cactbot 0.37.5 与随包 ACT 插件（含 Triggernometry 2.3.0.0）均为上游当前版本。

## 0.4.5.17 发布前核对（2026-10-30）

本版包含多语言布局、野外木人统计、近期战斗页、Host OwnerID、皮肤云同步兼容修复与同进程插件加载死代码清理，不改随包依赖。
三项发布依赖门禁重新联网核对均通过：`sync-parser-dependencies.ps1 -Check` 确认 FFXIV_ACT_Plugin 3.0.3.4 / IINACT 2.10.3.11；
`sync-bundled-act-plugins.ps1 -Check` 确认随包 ACT 插件仍为当前版本（含 Triggernometry 2.3.0.0）；
`sync-cactbot.ps1 -Check` 确认 Cactbot 0.37.5 与官方摘要一致。未同步上游依赖或修改锁文件。
