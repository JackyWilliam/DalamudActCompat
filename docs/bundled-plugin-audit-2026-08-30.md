# 随包插件与解析依赖走查（2026-08-30）

本次以仓库锁定文件、随包二进制版本/哈希和维护方公开源为准。结论分为“需要更新”“需要适配”和“无需变更”，避免把游戏内或远端的版本提示直接当成发布版本。

## 随包 ACT 插件

| 组件 | 随包版本 | 上游核对 | 结论 |
| --- | --- | --- | --- |
| Triggernometry CN | 2.1.2.2 | [MnFeN/Triggernometry](https://github.com/MnFeN/Triggernometry) 不发布 GitHub Release，维护版通过仓库 README 中的直链分发。用户新下载的 DLL 与随包文件版本、长度和 SHA-256 完全相同 | 无更新；游戏内 `.3` 提示为误报，不改包 |
| ACT.FoxTTS | 3.3.1.189 | [最新 Release 仍为 3.3.1](https://github.com/Noisyfox/ACT.FoxTTS/releases/tag/3.3.1) | 无更新，无新增适配 |
| PostNamazu | 1.3.6.6 | [最新 Release 仍为 1.3.6.6](https://github.com/Natsukage/PostNamazu/releases/tag/1.3.6.6) | 无更新；现有 Host 权限/命令桥继续适用 |
| SilverDasher | 0.6.0.4 | 完整包版本不变；公开数据端的国际服 `InitZone`、`ActorControlSelf` 仍落后于 7.55h2 | 需要适配：更新随包 opcode 种子，并在读取下载数据后归一化，防止远端旧值覆盖 |
| Cafe.Matcha | 26.8.12.1622 | 随包源码基线仍为 [thewakingsands/matcha@6cf242b](https://github.com/thewakingsands/matcha/tree/6cf242b59475aa77e4c2deee61e1b9191be5ba13)，其 Global 表仍是 7.55h | 需要适配：Host 加载时注入已验证的 Global 7.55h2 映射；没有权威值的 `FateInfo`、`WorldVisitQueue` 不保留冲突旧值 |

## 游戏内解析链

| 组件 | 调整前 | 调整后 | 说明 |
| --- | --- | --- | --- |
| IINACT | 2.10.3.5 | 2.10.3.6 | 对齐 [官方 v2.10.3.6](https://github.com/marzent/IINACT/releases/tag/v2.10.3.6)，保留本仓库区域服和 Host 兼容补丁 |
| OverlayPlugin Core | 0.19.104 | 0.19.105 | 对齐 [官方 v0.19.105](https://github.com/OverlayPlugin/OverlayPlugin/releases/tag/v0.19.105)；7.55h2 opcode 已核对 |
| Unscrambler.XIV | 7.55.1 | 7.55.2 | 7.55.2 已包含 `2026.08.11.0000.0000` Global h2 资源 |
| Machina | 7.55h2 fork | 不变 | 当前锁定提交已经包含 Global 7.55h2 与国服数据，不回退到仅国际服的上游提交 |
| FFXIV_ACT_Plugin | 3.0.2.8 | 不变 | 二进制已经是 3.0.2.8；只修正文档中误写的 3.0.2.7 |

## 其他结论

- Cactbot 不作为固定 DLL 随包，它由官方 Release ZIP 的独立安装/校验流程维护，本次没有发现需要改宿主接口的新问题。
- 双开 ACT / 同时启动两套解析链不列为缺陷；该用法本身不受支持。
- 命中统计的 ACT 集合遍历已经位于 `ActionDataLock` 内，复核后无需重复加锁或额外快照。
