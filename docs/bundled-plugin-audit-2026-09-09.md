# FF14 7.56 与分发插件审计

核对日期：2026-09-09；2026-09-10 补充本机国服验收。基线为公开版 0.4.1.0，本次版本为 0.4.1.1。
0.4.1.1 已安装，用户确认游戏内运行正常并授权发布；正式发布状态以 GitHub Release 为准。

| 组件 | 原版本 | 本次结果 |
| --- | --- | --- |
| FFXIV_ACT_Plugin 与 SDK | 3.0.2.8 | 更新至 [3.0.3.0](https://github.com/ravahn/FFXIV_ACT_Plugin/releases/tag/3.0.3.0)，上游明确支持 7.56 |
| IINACT fork | 2.10.3.6 | 对齐 [2.10.3.7](https://github.com/marzent/IINACT/releases/tag/v2.10.3.7)，保留 DACT 的区域、统计、Host 与日志补丁 |
| OverlayPlugin Core | 0.19.105 | 对齐 [0.19.107](https://github.com/OverlayPlugin/OverlayPlugin/releases/tag/v0.19.107) 的操作码和 SDK 更新；DACT 内嵌源码端口，不替换成外置 ACT 的 DLL |
| Unscrambler.XIV | 7.55.2 | 更新至 [7.56.0](https://www.nuget.org/packages/Unscrambler.XIV/7.56.0)，包含完整 7.56 常量与嵌入资源 |
| Machina fork | 7.55h2 | 更新国服、国际服、韩服各 26 项操作码；繁中表保持原区域数据 |
| Triggernometry CN | 2.1.2.2 | 实际分发 DLL 更新到 2.2.0.1；适配 2.2 区域事件接口及切区后的日志实体缓存清理 |
| Cactbot | 0.37.5 | [最新正式包](https://github.com/OverlayPlugin/cactbot/releases/tag/v0.37.5)不变；[7.56 数据更新](https://github.com/OverlayPlugin/cactbot/commit/8fd80bbeb6bbfbc17a341ddc8eddc4a0d04f8012)和[驯兽师数据](https://github.com/OverlayPlugin/cactbot/commit/b228a6fee96bdec37fcbf494c02a0c7f2f448157)仅在源码中，等待上游正式包后再同步 |
| PostNamazu | 1.3.6.6 | [正式 Release](https://github.com/Natsukage/PostNamazu/releases/tag/1.3.6.6)不变，继续现有 Host 命令桥 |
| ACT.FoxTTS | 3.3.1.189 | [正式 Release](https://github.com/Noisyfox/ACT.FoxTTS/releases/tag/3.3.1)不变，下载哈希一致 |
| SilverDasher | 0.6.0.4 | 保留经授权固定包；Host 更新 InitZone、ActorControlSelf，并防止旧在线数据覆盖。公开入口不足以证明存在更新的完整分发包 |
| Cafe.Matcha | 26.8.12.1622 / DACT3 | [上游源码](https://github.com/thewakingsands/matcha/tree/6cf242b59475aa77e4c2deee61e1b9191be5ba13)未变；Host 同时更新 China/Global 的 21 项映射，保持包内兼容构建与授权信息 |
| CactbotSelf / MoreLogLine（仅支持用户安装） | 不随包 | [上游最新公开 Release](https://github.com/tssailzz8/cacbotSelf/releases/tag/7.25)仍标为 7.25，未发现可宣称支持 7.56 的新版；原生摄像机/标点等功能需单独实机验证 |

## 操作码与解混淆

- Machina Global 表采用 [marzent/machina@3600c03](https://github.com/marzent/machina/commit/3600c03883f4aa6f76f057c47b97078c3ecec72e)。
- [FFXIVOpcodes@040fafa](https://github.com/karashiiro/FFXIVOpcodes/blob/040fafa385c991d5d1624097125f49bdd0d1781f/opcodes.json)标明 Global/CN/KR 均为 7.56，三个 ServerZoneIpcType 表逐项一致；OverlayPlugin 也分别发布了三地服 7.56 表。
- 解混淆常量来自 NuGet 包所标注的 [Unscrambler 源码提交](https://github.com/perchbirdd/unscrambler/blob/7ddf69f7f04a68e96458689680018e7e4f637c38/Unscrambler/Constants/Versions/7.5/Constants756.cs)：客户端版本 `2026.09.01.0000.0000`，19 项混淆操作码，密钥表大小 `193 × 4 = 772` 字节。国服使用实际扫描出的表地址，不能复用国际服 `0x2312040` RVA。
- 抹茶别名沿用原协议语义：`ResumeEventScene32` 对应 `MiniCactpotInit`，不是 `EventPlay32`；客户端市场板请求保留 `0x8000` 方向位。
- 上游未给出新的 `FateInfo`、`WorldVisitQueue` 值。抹茶两地服均移除旧键；银山雀儿两地服使用原国际服的越界占位 `0xF009`。它不是已经验证的新网络操作码。旧国服 `FateInfo=0x00E9` 在 7.56 已是 Waymark，继续使用会错误处理场地标点。银山雀儿的 ActorControlSelf FATE 开始/结束/进度路径保留。

## Triggernometry 2.2 兼容

[2.1.2.2 到 2.2.0.1 源码差异](https://github.com/MnFeN/Triggernometry/compare/b0f571a70e3593fc838d563e0af11fb02ea8ea6d...e030ec2fd872adb4cca72dac9aba8f59d6a65815)包括区域订阅改为事件、日志转写器、仓库元数据替换、职业数据和 7.56 VFX 签名。DACT 的旧方法精确匹配在真实 DLL 上失败，现保留旧入口并接受新版绑定委托的 owner，沿用弱引用订阅和卸载清理。切区时显式清理新版日志实体缓存，防止 Host 提前更新 currentZone 后跳过上游清理。

新版 DLL SHA-256：`ea764f6e2ba1a8955574efd9cbf744948e82f0986d15faa3768d07d97a85e5e8`。
原生地面 VFX 的更新函数由 Dalamud 提供的 FFXIVClientStructs 地址解析；本项目没有照抄上游另一套 StaticVfxRun 调用点签名。更新后的本机 SDK 的创建/更新签名与 DACT 的移除、Actor VFX 创建/销毁签名均在新版国服客户端唯一命中；实际绘图仍须实机验收。

## FFLogs 日志版本

重打解析器补丁、同步运行时与 SDK 的 Logfile.dll，并实际调用最终运行时 DLL 的 `LogFormat.FormatVersion()`。期望输出为：

```text
This is IINACT 2.10.3.7 (API 1.6.0) based on FFXIV_ACT_Plugin 3.0.3.0
```

这里的 `API 1.6.0` 是 IINACT 解析接口版本，不能改成 Dalamud API 15。
不修改用户已有 Network 日志、不伪造版本头或上传结果；新日志随候选运行时生成。
回归同时验证未知版本和不匹配密钥表不能进入已验证解混淆路径。离线测试不等同于 FFLogs 服务端排名验收。

## 验证边界

对相关源码与上游差异进行本地审查，并运行解析/包、真实 DLL Host、Legacy Resource 和 Scanner 回归。Host 检查使用本轮实际加载的改写 DLL 路径，避免共享缓存中较新的资源转换 DLL 导致误报。

国际服额外使用项目 CI 的[官方参考程序集](https://goatcorp.github.io/dalamud-distrib/stg/latest.zip)进行独立构建：Dalamud `15.0.3.4+cc568d5616baeb3c32e13acecd9171881a122501`，下载 ZIP SHA-256 为 `2343ab848def4f749a8b7ecf9a139e82f38801a8f631f6b55caf748c768aab4c`。官方 stable/staging 版本元数据当时均为 15.0.3.4。补充回归验证四个国际服原生客户端语言码 × 五种显示语言（包含中文）均选择 Global 7.56 配置，并调用实际 Unscrambler DLL，将合成 ActorCast/ActionEffect 报文还原为已知技能 ID、伤害字段且不改写其他字节。国际服使用官方密钥表 RVA `0x2312040`；国服继续动态扫描。本机未配置可用的国际服客户端，未声称国际服游戏内实测。

用户更新后，本机国服游戏文件为 `2026.09.01.0000.0000`，客户端 SHA-256 为 `64e07db25f0b72f9bc2dbd6ff7fb3725095ab251d928dd70bf98b0a8cebc6893`。只读 PE 检查确认 ZoneDown 签名恰有 3 个匹配，PacketDispatcher 虚表签名唯一匹配，顺着 OnReceivePacket 找到国服密钥表 RVA `0x230DD10`、大小 `772` 字节，满足本次配置条件。VFX 相关签名唯一匹配，SDK 字段布局仍为 `0x38/0x50/0x60/0x70/0x260`。使用刚更新的卫月/FFXIVClientStructs 构建通过。

2026-09-10 本机国服启动日志确认解析器 3.0.3.0、国服动态密钥表 `0x230DD10` / `772` 字节及原生地面 VFX 后端成功初始化。实际新生成的 `Network_30300` 日志首行与上述 IINACT/解析器版本头一致，用户确认 0.4.1.1 游戏内运行正常。

该确认不等同于国际服游戏内实测、所有副本机制逐项验收或 FFLogs 服务端上传/排名验收；这些场景及缺少上游值的可选事件仍保留验证边界。
