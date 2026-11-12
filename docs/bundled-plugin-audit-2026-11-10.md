# 7.58 网络兼容修复（1.0.0.4）

国服客户端在 2026-11-10 更新到 `2026.11.03.0000.0000`（7.58，与国际服、韩服同一构建）。
1.0.0.3 启动解析器时抛 `No supported zone receive signature for game 2026.11.03.0000.0000`：
自有接收签名版本门只认 7.57 两个版本号，新版本落回 7.57 以前的旧签名，而该旧签名在新客户端中
不存在。因此战斗统计、日志与依赖解析器的扩展全部无数据。

## 来源和改动

| 环节 | 本次版本 | 固定依据 |
| --- | --- | --- |
| IINACT | 2.10.3.12，保留 DACT 修改 | [官方版本差异](https://github.com/marzent/IINACT/compare/v2.10.3.11...v2.10.3.12)，逐个摘取上游 4 个提交（不含 CI 的 repo.json 与 machina 子模块指针） |
| 接收签名 | 7.58 沿用 7.57 两个精确编译变体 | 上游 2.10.3.12 签名未变；离线扫描本机国服 7.58 `ffxiv_dx11.exe`：两个变体各恰好 3 处命中，第三处都调用 RVA `0x1D68850`；7.57 以前的旧签名 0 处命中 |
| OverlayPlugin Core | 0.19.112，国服/国际服/韩服 7.58 同一张表，倒计时包国服/韩服改用 v730 结构 | [正式版本](https://github.com/OverlayPlugin/OverlayPlugin/releases/tag/v0.19.112) |
| Unscrambler.XIV | 保持 7.57.1 | NuGet 尚无 7.58；上游 IINACT 2.10.3.12 同样保持 7.57.1 并走运行时兜底 |
| Machina | 保留 DACT 分支，三地区 26 项表统一为 7.58 | Global 取自 [marzent/machina 26307e3](https://github.com/marzent/machina/commit/26307e30097f4821a13044aac607ae81075b5bde)（上游只更新了 Global）；[FFXIVOpcodes PR #300](https://github.com/karashiiro/FFXIVOpcodes/pull/300)（`a0ffb40`，未合并）国服 182 项、韩服 194 项与国际服逐项相同，其中与 Machina 重叠的 19 项全部一致 |
| 抹茶 | 保持 DACT 兼容构建，Host 两地区统一重写为 7.58 的 26 项 | FFXIVOpcodes PR #300；CEDirector `0x26E` 同时与 OverlayPlugin 7.58（622）一致 |
| 银山雀儿 | Host 归一化：InitZone `00AB`、ActorControlSelf `00A9`，FateInfo 回到占位 `F009` | 同上 |
| FFXIV ACT Plugin | 3.0.3.4 不变，Logfile 身份重写为 IINACT 2.10.3.12 | 官方 latest 仍为 3.0.3.4 |

## 解混淆：7.58 暂走运行时兜底

Unscrambler.XIV 还没有 7.58 配置。两地区都不会被当作「已验证配置」，而是：

- 运行时扫描本客户端密钥表（国服 7.58：`0x231F9A0`，34 × 4 = 136 字节，后面紧跟模块指针，结构正常）；
- 混淆操作码取 Machina 7.58 表中的 12 项（PlayerSpawn、NpcSpawn/2、Ability1/8/16/24/32、
  StatusEffectList/3、ActorControl、ActorCast），与上游 IINACT 2.10.3.12 的兜底一致；Examine、
  UpdateGearset、UpdateParty、UnknownEffect01/16、ActionEffect02/04 不在 Machina 解析范围内，留空。
- 启动时保留「ACT 网络兼容性警告」：战斗解析可用，但在官方配置发布并核对前，新日志的
  FF Logs 排名有效性不作保证。Unscrambler 发布 7.58 后应改为已验证配置并去掉警告。

## 没有公开值、暂不解析的包

7.58 的 FateInfo 与 WorldVisitQueue 在 FFXIVOpcodes、抹茶上游都还没有值（抹茶上游最后一次更新
是 10 月 15 日）。沿用 7.57 的编号会把无关包当成 FATE / 排队数据，因此两地区都不映射：
抹茶的 FATE 提醒、跨服排队提示，以及银山雀儿依赖 FateInfo 的功能，在值公开前暂不可用。

## 验证与边界

- Scanner：7.58 版本接受两个 7.57 变体、拒绝旧签名，不足三处匹配失败。
- Package：7.58 无 Unscrambler 配置时三地区都走兜底，兜底操作码与 Machina 7.58 一致，并用
  34 项密钥表合成解码 ActorCast/ActionEffect（含伤害字段）；Machina 三地区 26 项、OverlayPlugin
  三地区 12 项、IINACT/OverlayPlugin 版本与 FFLogs 头部 `This is IINACT 2.10.3.12 ...` 逐项断言。
  7.57/7.57h 的 Unscrambler 配置仍随包并单独测试，但已不再与 Machina 当前表比对。
- Host：真实抹茶 DLL 两地区重写为 7.58 表、钓鱼生命周期、旧 FateInfo/WorldVisitQueue 编号不再被识别；
  银山雀儿归一化为 7.58。
- 以上为离线验证。实际 7.58 客户端中的初始化、战斗统计、日志上传与扩展事件需在游戏内确认；
  PR #300 合并后需复核数值是否有变。

# 1.0.0.5 补齐：解析器 3.0.3.5 与 Unscrambler 7.58.0

1.0.0.4 发布（12:16Z）之后，上游补出了 7.58 剩下的两项依赖。本节只记录这两项，其它依赖与 1.0.0.4 相同
（三道依赖门禁本地复核：解析器、随包扩展、Cactbot 0.37.5 均为最新）。

| 环节 | 1.0.0.4 | 1.0.0.5 | 固定依据 |
| --- | --- | --- | --- |
| FFXIV ACT Plugin | 3.0.3.4 | 3.0.3.5 | [官方发布](https://github.com/ravahn/FFXIV_ACT_Plugin/releases/tag/3.0.3.5)（2026-11-11 00:58Z，附件 SHA-256 `9e1487831e9b9d89a4452cb75cec4cbef578ab43b2246733eb1ecce03cb28011`）；`tools/sync-parser-dependencies.ps1` 同步，Logfile 身份仍重写为 IINACT 2.10.3.12 |
| Unscrambler.XIV | 7.57.1（7.58 走兜底） | 7.58.0 | [NuGet](https://www.nuget.org/packages/Unscrambler.XIV/7.58.0)（2026-11-10 15:26Z，packageHash 与本地一致，登记源码 perchbirdd/unscrambler `c5826c2`）；锁文件 contentHash `1Z1tQimX…OGig==` |

## 解析器来源核对

同步工具从 iinact.com updater 下载。把官方 GitHub 发布包放进临时目录、走同一套解包与补丁流程，
得到的 15 个文件（主程序、SDK 与顶层副本）与仓库内文件逐字节一致。Resource 从 8,670,208 增至
8,714,240 字节（繁中地名），其余文件大小不变。

## 解混淆：7.58 改为已验证配置

Unscrambler 7.58.0 的 `Constants758`：GameVersion `2026.11.03.0000.0000`、密钥表 RVA `0x2322350`、
34 × 4 = 136 字节、ObfuscationEnabledMode 157、InitZone `0xAB`、UnknownObfuscationInitOpcode `0x2F5`，
19 项混淆操作码，工厂对应 `Unscrambler73`。

- **国际服**：`CanUseBundledVersionConstants` 直接命中 7.58 官方配置，不再兜底、不再弹警告。
- **国服**：版本号相同，但国服 7.58 客户端的密钥表在 `0x231F9A0`（1.0.0.4 离线扫描结果，同为 136 字节），
  不能用国际服地址。`CanUseChineseRuntimeVersionConstants` 加入 7.58：使用官方 19 项操作码，密钥表从运行中的
  客户端扫描，并要求大小等于 136 字节（7.57 的 83 项等其它大小会被拒绝）。
- **国服 19 项逐项依据**：公开国服 7.58 操作码 [Yarukon/FFXIVNetworkOpcodes `725b4b0`](https://github.com/Yarukon/FFXIVNetworkOpcodes/tree/725b4b0/output/CN_2026.11.03)
  （Unscrambler 代码生成器取国服操作码的同一来源）。其中 12 项取自 machina.txt，Examine / UpdateGearset(ModelEquip) /
  UpdateParty 取自 opcodes.json，均与 Unscrambler 7.58 相同；InitZone 同为 `0xAB`。
  UnknownEffect01/16、ActionEffect02/04 四项按生成器写死的名字（EventAction8/16、EventFinish64/128）对不上，
  但该数据集这四个名字整体错开一位：按 EventAction16/32、EventFinish128/255 读取时，国服 7.57 数据与已验证的
  Unscrambler 7.57 四项完全相同；同一映射下国服 7.58 为 `0x351`、`0x385`、`0x326`、`0x222`，与 Unscrambler 7.58 一致。
- 1.0.0.4 兜底填入的 12 项 Machina 值与官方 7.58 完全相同；当时留空的 7 项现在有了官方值。

## 验证与边界

- Package：`Patch758SmokeTests` 改为断言官方 7.58 常量、工厂类型、国际服走打包配置、国服走运行时密钥表
  （拒绝 132/140/332/356 字节）、国服运行时常量不借用国际服地址与查表、Machina 国服/国际服 12 项与官方一致、
  翻译客户端仍选国际服配置，并用两地区解码器完整还原合成 ActorCast / ActionEffect 包；
  依赖版本断言 Unscrambler 7.58.0.0、FFXIV_ACT_Plugin 3.0.3.5，FFLogs 头为
  `This is IINACT 2.10.3.12 (API 1.6.0) based on FFXIV_ACT_Plugin 3.0.3.5`。
- 以上为离线验证。7.58 游戏内的初始化（日志应出现 bundled / runtime-discovered Chinese key table，而不是
  No ranking-safe）、战斗统计与 FF Logs 上传仍需实测。韩服不在 DACT 发行范围内，未作结论。

# 1.0.0.7 补齐：抹茶 / 银山雀儿的 FateInfo 与 WorldVisitQueue

2026-11-11 复查（对照 1.0.0.6）：解析器、IINACT、OverlayPlugin、Unscrambler、Machina、FFXIVOpcodes、Cactbot、
随包扩展均无新版本，三道依赖门禁 current。唯一变化是抹茶上游补出了 7.58 的两个编号。

| 环节 | 1.0.0.6 | 1.0.0.7 | 固定依据 |
| --- | --- | --- | --- |
| 抹茶 FateInfo | 不映射 | `0x0374` | [thewakingsands/matcha `fb92f67`](https://github.com/thewakingsands/matcha/commit/fb92f67)（2026-11-12 02:31Z，CI 生成的 7.58 表，国服/国际服同值） |
| 抹茶 WorldVisitQueue | 不映射 | `0x0356` | 同上 |
| 银山雀儿 FateInfo | 占位 `F009` | `0x0374` | 与抹茶 FateInfo 为同一个包（7.57 时两者同为 `0x0259`） |

## 来源核对

- 抹茶上游表共 30 项，其中 26 项与 DACT 1.0.0.4 起的 7.58 重写表逐项相同；UpdateInventorySlot、Logout 两项
  DACT 基线 `d4fffc6` 的枚举里没有，不涉及。
- 抹茶 CI 从 [zhyupe/ffxiv-opcode-worker `91303cd`](https://github.com/zhyupe/ffxiv-opcode-worker/commit/91303cd) 取国服表。该提交的
  `reports/7.58.json` 标注这两项来源为 opcodediff（由 7.57→7.58 二进制差异一对一映射），并在国服
  `2026.11.03.0000.0000` 客户端（exe SHA-256 `83982c85…`）上核对了 201 个具名 Zone 操作码。它的 7.57 表
  FateInfo `0x0259`、WorldVisitQueue `0x007C` 与 DACT 7.57 国服所用值（抹茶 `0a5fb86`）相同。
- FFXIVOpcodes master 的 7.58 表仍不含这两项；`0x0374`、`0x0356` 在其国服/国际服 7.58 表和 DACT 的
  Machina / OverlayPlugin 7.58 表中都没有被其他包占用。

## 验证与边界

- Host：抹茶两地区 7.58 表 28 项逐项断言；恢复 1.0.0.4 前的路由检查——真实抹茶 DLL 把 `0x0374` 解析为 FATE
  （开始时间、时长）、把 `0x0356` 交给排队处理器（位置、等待时间）；7.57 旧编号、`F009` 占位与反方向包不识别。
  银山雀儿 opcodes.json 两地区 FateInfo 归一化为 `0x0374`。
- 两个编号暂无第二份独立公开表，FATE 提醒与跨服排队需在游戏内确认。
