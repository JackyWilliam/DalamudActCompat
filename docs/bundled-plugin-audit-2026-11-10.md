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
