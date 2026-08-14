# FFLogs rDPS Parity Harness

该工具从 FFLogs v2 public API 抓取真实 DNC 击杀样本，并通过 production
`RaidDpsEstimator` 重放。它的职责是暴露差异，不会修改公式或用最终 rDPS
反推 adaptor 补偿。

```text
FFLogs API events
        ↓
FflogsEventNormalizer
        ↓
RaidDpsEstimator (production)
        ↓
ParityReportWriter
```

## 数据口径

- `characterRankings` 仅用于发现公开 report/fight，不作为 parity ground truth。
- FFLogs `DamageDone` table 的 `totalRDPS`、`totalRDPSTaken`、
  `totalRDPSGiven`、`totalADPS`、`totalNDPS` 是参考总量。
- FFLogs 与 DACT 都使用 `DamageDone.totalTime` 作为报告中的共同分母。
- downtime 分类使用同一张表的 `damageDowntime`，不从最终 rDPS 反推。
- `damage.amount` 提供有效伤害；相同 packet ID 的 `calculateddamage.timestamp`
  提供 DACT action packet 时间。DoT tick 没有对应 calculateddamage 是预期行为。
- FFLogs status ID 的一百万命名空间只做机械还原；Technical Finish 的 legacy
  action ID 只做当前网络 action ID 映射。Normalization 不读取或补偿最终 rDPS。

FFLogs 官方数学参考：<https://www.fflogs.com/help/rdps>。

## 使用

凭据优先读取 `FFLOGS_CLIENT_ID` / `FFLOGS_CLIENT_SECRET`，否则读取 DACT
配置中的 FFLogs client。凭据和 token 不会写入缓存。

```powershell
dotnet run --project tools/DalamudActCompat.FflogsParityHarness -c Release -- --samples 100
dotnet run --project tools/DalamudActCompat.FflogsParityHarness -c Release -- --samples 100 --collect-only
dotnet run --project tools/DalamudActCompat.FflogsParityHarness -c Release -- --samples 100 --replay-only
dotnet run --project tools/DalamudActCompat.FflogsParityHarness -c Release -- --self-test
dotnet run --project tools/DalamudActCompat.FflogsParityHarness -c Release -- --devilment-probe
dotnet run --project tools/DalamudActCompat.FflogsParityHarness -c Release -- --guaranteed-hit-experiment
dotnet run --project tools/DalamudActCompat.FflogsParityHarness -c Release -- --attribution-matrix
dotnet run --project tools/DalamudActCompat.FflogsParityHarness -c Release -- --percentage-audit
dotnet run --project tools/DalamudActCompat.FflogsParityHarness -c Release -- --percentage-ordering-only
```

`--percentage-ordering-only` is cache-only. It reads production per-event component counters,
compares parameter-free percentage/rate ordering counterfactuals, and emits packet-sequence,
expiry, Technical eligibility, direct/periodic, and conservation reports. It never modifies
the production attribution equation.

The same mode also emits the strict direct-normal core, explicit interaction decomposition,
provider-level identifiability, cross-provider validation, and live-causal status fallback
comparison in `percentage-identification-*` and `percentage-status-*` reports.

`--percentage-audit` 只读取现有 106 场 cache，逐 provider/recipient 对账 fixed-magnitude
percentage contribution，并输出 reference direction、single/overlap、event-state、DoT snapshot
和 provider 类型诊断。FFLogs public API 不提供 per-event/per-window contribution；报告不会从整场
`given[]` 反推不存在的逐事件 truth。

`--guaranteed-hit-experiment` 只读取现有 100 场缓存。它从 61 场 SAM partner 中选取
30 场 guaranteed damage 最高的高信息量样本，并把 production 实测的 regular-action
Devilment contribution 保持不变，只离线替换 guaranteed-event candidate contribution。
比较基准只能是 FFLogs 整场 `Devilment given.total`；工具不会伪造或反推 per-action truth，
也不会写回 production `TransferGuaranteedCriticalDirectContribution`。

`--devilment-probe` 只读取现有 manifest、原始 event cache 和第一轮
`parity-report.json`。它固定选择 6 个 SAM 与 5 个 DRG 样本，输出逐 action、逐 window、
guaranteed coverage、component parity 和 Cu/Du sensitivity 文件。FFLogs API 未提供的
per-action/per-window contribution 保持为 `unavailable_api`；diagnostic scenario 不会写回
normalization 或 production `RaidDpsEstimator`。

原始 GraphQL JSON 默认缓存在
`artifacts/fflogs-parity-harness/cache`。JSON、CSV 和 Markdown 报告默认写到
`artifacts/fflogs-parity-harness/reports`。整个 `artifacts` 目录已被 Git 忽略，
避免提交大体积战斗数据和公开玩家名称。

采集器会缓存每个原始响应、分页拉取 events、原子写缓存、保留 rate-limit
余量并对瞬时 HTTP/GraphQL 失败重试。`--refresh` 会忽略现有响应缓存。

## 当前边界

- 第一轮范围固定为 CN、AAC Heavyweight、difficulty 101、partition 9，按四个
  encounter 分层抽样。
- API events 没有 DACT 的完整 NameToggle/targetability 原始包；目标可选时长
  parity 单独记录，不在 adaptor 中伪造。
- production estimator 目前按来源聚合 DNC percentage contribution，因此只能
  直接对比 Technical + Standard 合计；FFLogs 的两个分项仍单独保留在报告中。
- DNC 尚未达到 production parity，不能标记 frozen。

## 第一轮公式审计

与 FFLogs 公开说明逐项对照后：

- production 的 percentage multiplier 乘法叠加、lost damage 和 log-weighted
  分摊结构与公开公式一致。
- regular hit 与 simulated DoT 的 Crit/DH 分摊结构也与公开公式一致；self
  contribution 会排除，pet damage 会回到 owner。
- 尚未 parity 的首要数学输入差异是 `C_u` / `D_u`：FFLogs 公式要求玩家真实的
  unbuffed Crit/DH chance，DACT 当前只能用 `25%`、40-hit prior 和观测命中估计，
  再限制到 5%–50%。这不是 FFLogs 公开公式的一部分。
- guaranteed Crit/DH 的 production action 表和 rate-buff scaling 是额外实现层；
  第一轮样本中 SAM partner 的 Devilment 负差显著，必须优先逐 action 对照，不能
  用最终差值调整 multiplier。
- buff packet 时序、死亡清状态、DoT application/refresh snapshot 和 pet/owner
  状态继承仍属于 event/state parity，不能由最终 rDPS 接近来判定正确。

本轮只记录这些差异，没有修改 production `RaidDpsEstimator`。
