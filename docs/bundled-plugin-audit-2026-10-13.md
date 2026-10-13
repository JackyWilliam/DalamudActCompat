# 7.57 网络兼容修复（0.4.5.12 候选）

用户在国服更新后遇到 `Parser initialization failed`，缺失签名为
`E8 ?? ?? ?? ?? 4C 8B 4F 10 8B 47 1C 45`。0.4.5.11 仍使用 7.56h
签名、操作码和解混淆资源；单独重启解析器不能替换这些编译进程序的数据。

## 来源和改动

| 环节 | 当前候选 | 固定依据 |
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
提交、抹茶新增钓鱼统计功能和 Triggernometry 的新数学函数没有纳入候选。

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

当前为本地候选。正式发布、订阅更新与游戏安装须以实际执行结果为准。
