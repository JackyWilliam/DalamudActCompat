# 7.56 热修依赖审计（0.4.4.0）

目标客户端：`2026.09.15.0000.0000`。此前发布物仍使用 9 月 1 日事件表；新客户端 ActionEffect01 从 `0x2EC` 改为 `0x313`，旧解析器不能按原编号识别技能事件。四种坦克无敌的触发规则已经存在，本次更新解析输入所需的版本配置和事件表。

## 依赖来源

| 组件 | 本次使用 | 来源 |
| --- | --- | --- |
| FFXIV ACT Plugin | 3.0.3.1 | [正式发布](https://github.com/ravahn/FFXIV_ACT_Plugin/releases/tag/3.0.3.1)；官方 ZIP SHA-256 `9703ccf9f2937fb4840c42b1b82e1d4834f5e81ba9ec80e3c234f54d251525f5` |
| IINACT | 2.10.3.8，保留 DACT 分支修改 | [上游版本](https://github.com/marzent/IINACT/releases/tag/v2.10.3.8) |
| OverlayPlugin Core | 0.19.108 | [上游发布](https://github.com/OverlayPlugin/OverlayPlugin/releases/tag/v0.19.108)；事件表取 IINACT `v2.10.3.8` 的 `OverlayPlugin.Core/resources/opcodes.jsonc` |
| Unscrambler.XIV | 7.56.1，NuGet 锁定 | [包](https://www.nuget.org/packages/Unscrambler.XIV/7.56.1)、[源码提交](https://github.com/perchbirdd/unscrambler/commit/c4abc533dda9ab29afe0883854e1d9e1c4a9d69a) |
| Machina | DACT 分支 2.4.5.5 + 7.56h 事件表 | [事件表来源提交](https://github.com/ravahn/machina/commit/6bb98826caf2b445dadb0cb8eb40c4bd55ecb373)；仅同步 Global/CN/KR 三份表，未将整个分支替换成 NuGet 2.4.7.9 |
| Cactbot | 0.37.5 | 保留已有锁定资源 |

ACT DLL 经现有同步工具解包和兼容处理；日志头仍保留真实解析器身份，不使用伪造版本字符串。依赖 SDK 副本与运行副本一起更新。

## 地区和解混淆约束

- 7.56h 的混淆表长度为 165 个整数（660 字节），不是原 7.56 的 772 字节；19 个混淆事件均核对。
- 国际服使用官方配置。国服仍从当前客户端扫描专用密钥表地址，不复用国际服的固定 RVA `0x2312740`。仅在已核对版本、表长度及完整操作码满足约束时采用地区配置；未知版本或长度不匹配保持原有明确 fallback 提示。
- InitZone=`0x32B`、ActorControl=`0x25F`、ActorCast=`0x162`、ActionEffect01=`0x313`；Machina、Unscrambler 与 OP 的相关事件保持一致。
- 抹茶和银山雀儿同步对应映射；别名 CompanyAirshipStatus 对应 AirshipTimers，ResumeEventScene32 对应 MiniCactpotInit。MarketBoardItemRequest 保留出站方向高位；未经确认的 FateInfo/WorldVisitQueue 不填入猜测值。

## 验证与边界

- 国服扫描地址选择、拒绝国际服固定地址、旧表长度和未来未知版本的回归。
- 国服／国际服合成 ActorCast、ActionEffect 包，覆盖神圣领域（`1E`）、死斗（`2B`）、行尸走肉（`E36`）、超火流星（`3F18`）及宽技能编号；比较完整解码包和伤害字段，检查无额外写入。
- 构建、完整 Package/Host 回归及实际 cimgui + 本机 SqPack 的皮肤和好友界面回归；发布流水线再次运行扫描、Host、仿生石、U7b 初始化、绘图诊断及资源检查。
- 合成解码不是游戏内施放验收。语音、字幕和真实副本绘图仍需更新后验证，不能据此宣称所有绝伊甸／绝妖星缺圈均已修复。

本次公开版同时包含已完成的皮肤、统计背景、弹窗与底边修正，以及团队秒伤。未覆盖本机安装，保留用户的稍后安装安排。
