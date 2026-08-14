# FFLogs percentage × rate ownership 与 causal status audit

日期：2026-08-14

## 结论

- percentage × Crit/DH interaction ownership：`Not determined`。
- `SharedBaseLog` 与 `SharedShapley`：`YES`，可由当前 provider-level aggregate observations 区分；接近的全局 MAE 不代表 non-identifiable。
- 没有一个无参数 ownership candidate 能同时跨 provider、actor、encounter、Crit/DH dimension 稳定胜出，因此 production ownership 未修改。
- live status：采用 individual packet-aware lifecycle；nominal endpoint 后最多等待既有 2 秒 action/status protocol horizon，期间 remove/refresh/reset packet 优先，超时进入 fallback expiry。
- causal individual fallback：`causal / bounded / missing-remove-safe`，已作为独立 production commit `f87351b` 实施。
- guaranteed equation identification 前提：`NO`。本轮到此停止，没有修改 guaranteed Crit/DH/CDH equation、prior、clamp 或 Life Surge。

## 范围与证据

本轮只读取原有 cache：原 100 场加既有 targeted 6 场，共 106 场；没有网络采样，也没有扩大普通 corpus。复用了上一轮 ordering harness 和全部 percentage ordering / expiry / Technical / conservation artifacts。

Primary clean set 固定为 262 条 direct-normal rate-overlap aggregate constraints，覆盖 71 reports、74 fights、100 recipients、150 providers、28 dominant action families。每条均保留 report/fight/actor/action/provider、raw/effective damage、percentage/rate aggregate references、buff composition、packet sequence 与 status state source。

262/262 同时满足：

- direct only；无 periodic、DoT 或 simulated tick；
- 无 known guaranteed Crit、DH、CDH；
- 无 death/reset boundary；
- pet owner 已解析且无 ambiguity；
- 无 unknown-magnitude percentage exposure；
- 无已知 metadata mismatch；
- percentage 与 recipient-level rate aggregate reference 均存在。

Provider job 分布为 DNC 124、AST 44、PCT 27、RDM 16、SMN 15、RPR 14、MNK 11、NIN 9、BRD 2。

## Interaction decomposition

令同一 damage event：

```text
N = B × M × C × D
```

其中 `B` 为 base damage，`M` 为 percentage multiplier；effective `C/D` 由固定的 current Crit/DH contribution 反解，仅用于把已有 rate math 写成守恒的三维分解，不拟合 FFLogs residual。

```text
Base                         = B
Percentage main              = B(M-1)
Crit main                    = B(C-1)
DH main                      = B(D-1)
Percentage×Crit              = B(M-1)(C-1)
Percentage×DH                = B(M-1)(D-1)
Crit×DH                      = B(C-1)(D-1)
Percentage×Crit×DH           = B(M-1)(C-1)(D-1)
```

八项之和严格等于 `N`。

| Term | PercentageFirst | RateFirst | SharedShapley2 | SharedShapley3 | SharedBaseLog |
|---|---|---|---|---|---|
| percentage main | Percentage | Percentage | Percentage | Percentage | global log pool |
| Crit main | Crit | Crit | Crit | Crit | global log pool |
| DH main | DH | DH | DH | DH | global log pool |
| percentage×Crit | Percentage | Crit | 1/2 Percentage + 1/2 Crit | 1/2 each | global log pool |
| percentage×DH | Percentage | DH | 1/2 Percentage + 1/2 DH | 1/2 each | global log pool |
| Crit×DH | 1/2 Crit + 1/2 DH | 1/2 Crit + 1/2 DH | 1/2 Crit + 1/2 DH | 1/2 Crit + 1/2 DH | global log pool |
| percentage×Crit×DH | Percentage | 1/2 Crit + 1/2 DH | 1/2 Percentage + 1/4 Crit + 1/4 DH | 1/3 each | global log pool |

`SharedBaseLog` 把全部 non-base damage 组成一个守恒 removal pool，再按 `ln(M) / ln(MCD)`、`ln(C) / ln(MCD)`、`ln(D) / ln(MCD)` 分配给三个维度。唯一新增 candidate 是 `SharedShapley3`：pair interaction 各分 1/2，triple interaction 各分 1/3。它无自由参数、保持 conservation，并与既有 percentage removal 及 normal Crit/DH math 兼容。

## Ownership candidate results

| Dataset | Candidate | N | Mean | Median | MAE | RMSE | Max | sign - / 0 / + |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| Rate overlap | PercentageFirst | 1,738 | +14,775.045 | +8,562.066 | 14,892.471 | 20,548.702 | 72,609.566 | 33 / 0 / 1,705 |
| Rate overlap | RateFirst | 1,738 | -11,277.271 | -7,208.017 | 11,464.732 | 15,631.573 | 84,185.808 | 1,683 / 0 / 55 |
| Rate overlap | SharedShapley2 | 1,738 | +1,748.887 | +610.716 | 2,853.924 | 5,071.232 | 59,864.382 | 536 / 0 / 1,202 |
| Rate overlap | SharedBaseLog | 1,738 | +1,713.022 | +625.980 | 2,838.172 | 5,054.051 | 59,395.755 | 533 / 0 / 1,205 |
| Rate overlap | SharedShapley3 | 1,738 | +1,657.854 | +603.455 | **2,789.016** | 4,984.331 | 60,076.099 | 539 / 0 / 1,199 |
| Direct-normal core | PercentageFirst | 262 | +17,678.504 | +9,114.222 | 17,678.504 | 23,662.176 | 71,264.823 | 0 / 0 / 262 |
| Direct-normal core | RateFirst | 262 | -13,145.113 | -7,949.598 | 13,866.103 | 18,257.108 | 59,405.379 | 256 / 0 / 6 |
| Direct-normal core | SharedShapley2 | 262 | +2,266.695 | +772.586 | 2,302.080 | 6,003.188 | 40,488.035 | 17 / 0 / 245 |
| Direct-normal core | SharedBaseLog | 262 | +2,151.030 | +775.423 | **2,176.484** | 5,890.309 | 40,032.398 | 16 / 0 / 246 |
| Direct-normal core | SharedShapley3 | 262 | +2,152.638 | +754.824 | 2,188.023 | 5,915.553 | 40,263.277 | 17 / 0 / 245 |

`SharedShapley3` 在完整集最优，`SharedBaseLog` 在 primary clean set 最优；差值很小且 winner 在分组后反复翻转，不能升级为通用 rule。

## SharedBaseLog residual localization

完整 1,738 条中最强 association 是 percentage×DH interaction：Pearson `0.524`、Spearman `0.574`，但 forced-through-origin explained fraction 仅 `0.357`。其次是 DH provider count / DH-rate total，均约 Pearson `0.513`、origin fraction `0.347`。

262 clean core 中同一信号显著减弱：

| Feature | Pearson | Spearman | origin explained fraction |
|---|---:|---:|---:|
| number of DH providers | 0.314 | 0.770 | 0.202 |
| percentage×DH | 0.313 | 0.808 | 0.200 |
| DH-rate total | 0.313 | 0.769 | 0.201 |
| Crit×DH | 0.277 | 0.790 | 0.170 |
| percentage×Crit×DH | 0.258 | 0.788 | 0.160 |
| percentage×Crit | 0.235 | 0.778 | 0.179 |
| buff age / distance to apply | 0.110 | 0.276 | 0.143 |
| distance to remove | 0.100 | 0.265 | 0.141 |
| same-timestamp status activity | 0.093 | 0.186 | 0.059 |

结论：residual 明显随 DH/Crit+DH exposure 分层，但没有任何一个 interaction term 与 residual 成比例。clean core 仍有 246/262 正 residual，window/timestamp variables 很弱；causal state 与 oracle 在 clean core 完全一致。因此约 2.8k MAE 不是单一 expiry、guaranteed 或 DoT 问题，也不是一个漏分配的守恒 interaction term可以解释。现有证据只支持“剩余结构集中于 DH/Crit+DH exposure、rate-input/aggregate mixing 或尚未观察到的 rate exposure state”，不能把其中任何一项确认成 FFLogs ownership 规则。

## Identifiability

答案是 `YES`。

- SharedShapley2 vs SharedBaseLog：完整集 1,736/1,738 个 aggregate predictions 差异超过 0.05 display tolerance，平均差 124.825，最大差 1,347.486。
- clean core：262/262 可区分，平均差 166.947，最大差 1,347.486。
- SharedBaseLog vs SharedShapley3：完整集 1,736/1,738 可区分；clean core 262/262 可区分。

代表性 discriminator：`yqcdfBD6LJwNY9Mz:1`（Red Hot / Deep Blue）MNK recipient 的 Divination aggregate，SharedShapley2 预测 445,945.848，SharedBaseLog 444,598.362，差 1,347.486；FFLogs reference 432,291.397，支持 SharedBaseLog。但同一 candidate 并未在所有 provider/actor/dimension 稳定胜出，所以“可区分”不等于“已确认”。

## Cross-provider validation

完整 1,738 条按 provider job 分组，SharedShapley3 在 8/9 个 job 的 MAE 最低，SharedBaseLog 在 1/9 最低；但 clean core 反转为 SharedBaseLog 6/9、SharedShapley3 3/9。按 actor，完整集 winner 为 Shapley3 292、BaseLog 255、Shapley2 14；clean core 为 BaseLog 70、Shapley3 30。按 encounter，完整集 Shapley3 为 4/4，clean core BaseLog 为 3/4、Shapley3 为 1/4。

Rate dimension 也不稳定：

- 完整 Crit-only：SharedShapley2/3 MAE 1,420.608，BaseLog 1,429.700。
- 完整 Crit+DH：Shapley3 5,885.170，BaseLog 6,024.917。
- clean Crit-only：BaseLog 932.428，Shapley2/3 945.974。
- clean Crit+DH：BaseLog 4,066.493，Shapley3 4,074.982。
- DH-only 仅 N=1，multiple DH 仅 N=4，clean separate Crit/DH providers 仅 N=1，不能支撑 universal DH rule。

因此不存在跨 provider、actor、encounter、Crit/DH dimension 稳定胜出的无参数 candidate。

## Matched interaction controls

在要求 same report + actor + provider + encounter + buff + dominant action family，并且 aggregate exposure 组合不同后，严格可用于 FFLogs provider aggregate truth 的 matched pairs 为 0。已有 event-local groups 没有 FFLogs per-event component reference，不能被提升为 equation evidence，也没有为凑 pair 重新抓取 corpus。

## Causal status state machine

Production lifecycle：

```text
Inactive
  -- apply --> ActiveObserved

ActiveObserved / ActiveRefreshed
  -- refresh/overwrite packet --> ActiveRefreshed
  -- nominal endpoint --> NominalExpiryReached --> PendingRemoveGrace
  -- remove packet --> RemovedObserved

PendingRemoveGrace
  -- remove packet --> RemovedObserved
  -- refresh/overwrite packet --> ActiveRefreshed
  -- existing 2s protocol horizon --> ExpiredFallback

Any active/pending state
  -- recipient death / encounter reset / actor reset --> Reset
```

Inactive 是 active dictionary 中不存在；Reset 会清空对应 live state。Damage eligibility 使用 half-open `[apply sequence, remove/fallback sequence)`，同 timestamp 由真实 packet sequence 决定，不只按 timestamp 排序。

### Strategy comparison

| Strategy | Live causal | Max stale/UI delay | Incorrect include | Incorrect exclude | Decision |
|---|---|---:|---:|---:|---|
| Nominal immediate | Yes | 0 ms | 0 | 5,768 events / 238,009,563 damage | Rejected：大量 delayed remove fan-out 被过早截断 |
| Individual pending + 2s fallback | Yes | 2,000 ms | 3 events / 26,529 damage | 0 | Selected |
| Cohort-evidence + 2s fallback | Yes | 2,000 ms | 0 | 2,559 events / 97,934,014 damage | Rejected：sibling remove fan-out 会过早截断其它 recipient |
| Oracle explicit packet | No | unbounded diagnostic | 0 | 0 | Diagnostic only；禁止用于 live |

2 秒不是 residual 拟合值，而是 production 已有 `PendingActionLifetime` action/status correlation horizon。15,386 个 explicit-remove intervals 中，8,278 个 remove 晚于 nominal；正延迟 P90/P95/P99 为 1,092/1,322/1,488 ms，最大 1,644 ms，全部落在 2 秒 bound 内。

28,324 intervals 的 individual strategy endpoint metrics：26,720 exact、4 early、1,600 late。1,600 late 是 missing-remove 的 bounded fallback，不是无限 stale；4 early 来自超过 horizon 的 refresh/overwrite，期间无 damage。唯一 3 个 fallback-only damage mismatches 全部是 Technical Finish，共 26,529 damage；Standard Finish、AST cards、Searing Light、Starry Muse 均无 causal-vs-oracle damage mismatch。

### Ownership 与 window 分离

| Dataset | Ownership | State | MAE | Gap from same-ownership oracle |
|---|---|---|---:|---:|
| Rate overlap | Current PercentageFirst | nominal | 13,000.347 | 3,057.506 |
| Rate overlap | Current PercentageFirst | oracle | 14,892.471 | 0 |
| Rate overlap | Current PercentageFirst | causal individual | 14,893.028 | 0.557 |
| Rate overlap | SharedBaseLog diagnostic | nominal | 4,271.336 | 2,979.087 |
| Rate overlap | SharedBaseLog diagnostic | oracle | 2,838.172 | 0 |
| Rate overlap | SharedBaseLog diagnostic | causal individual | 2,837.615 | 0.557 |
| Direct-normal core | SharedBaseLog diagnostic | nominal | 4,005.618 | 3,022.122 |
| Direct-normal core | SharedBaseLog diagnostic | oracle | 2,176.484 | 0 |
| Direct-normal core | SharedBaseLog diagnostic | causal individual | 2,176.484 | 0 |
| No-rate controls | Current/SharedBaseLog | nominal | 2,893.176 | 2,132.506 |
| No-rate controls | Current/SharedBaseLog | oracle | 1,185.963 | 0 |
| No-rate controls | Current/SharedBaseLog | causal individual | 1,186.579 | 0.616 |

当前 ownership 下 nominal window 的错误会抵消一部分 PercentageFirst over-attribution，所以修正 state 后 rate-overlap percentage-component MAE 反而上升；这不是回退策略变差。固定 SharedBaseLog 或 no-rate ownership 后，state 修正的独立改善清楚可见。ownership 从 PercentageFirst/oracle 到 SharedBaseLog/oracle 减少 12,054.299 MAE；SharedBaseLog 下 nominal 到 oracle 再减少 1,433.164 MAE。二者不可简单相加，但可以明确分离。

## Production patch gate

Ownership gate 未通过：candidate winner 对完整集与 clean core 不一致，按 provider/actor/dimension 会翻转，clean core 仍有 246/262 正 residual，DH-only / multiple-DH composition 太稀疏，也没有 strict matched aggregate control。因此 production ownership 保持 PercentageFirst。

Status gate 通过：individual strategy causal、最大 stale 2 秒、missing remove 可回退、packet remove/refresh/reset 优先、106-fight replay 可验证，并显著改善 no-rate 与 final rDPS。已单独提交 `f87351b`；未 push、PR、安装或发布。

## Regression

- Percentage audit：2,217 constraints，current overall MAE 10,816.614。
- Clean single-buff direct：8/8 exact，MAE 0。
- Clean multi-buff direct：22/24 exact，MAE 656.236。
- No-rate direct：109/111 exact，MAE 141.889；no-rate periodic MAE 1,500.888。
- Rate-overlap：1,738 constraints / 68,287 events；causal current ownership MAE 14,893.028；causal SharedBaseLog diagnostic MAE 2,837.615。
- Direct-normal core：262 constraints；causal SharedBaseLog MAE 2,176.484。
- Production percentage counter calibration：151,615 event constraints，145,379 exact，MAE 77.733。
- 106-fight attribution matrix：`EquationStatus = Not determined`。
- 原 100 samples：mean -320.548、median -306.060、MAE 327.076、max 1,089.019；status patch 前为 mean -359.325、median -350.242、MAE 364.088、max 1,151.696。
- Raw damage：100/100 exact；normalization warnings 0。
- Direct packet correlation：289,318 calculated direct packets matched，unmatched direct 0；67,064 unmatched calculated rows全部为 periodic rows。
- 既有固定 11-fight Devilment probe：1,582 action rows，通过。
- Guaranteed diagnostic：100 cached fights；production calibration 1,451 events，max event residual `9.82e-11`、max fight residual `4.66e-10`，`EquationStatus = Not determined`。
- Harness/ActRuntime targeted Release build：0 warning / 0 error；self-test passed。
- Package + FFXIV_ACT_Plugin、Host、Legacy Resource smoke：passed。
- Full Release solution：0 error；555 个既有 vendor/IINACT warnings。

## 最终九问

1. SharedBaseLog 剩余约 2.8k MAE 来自哪里？没有单一可确认 interaction term。最强结构是 DH/Crit+DH exposure，但 clean-core origin explained fraction 最高仅约 0.20；window 对 clean core 为 0，guaranteed/DoT 已排除。结论是 unresolved rate exposure/input/aggregate mixing，而不是已识别 ownership term。
2. SharedBaseLog 与 SharedShapley 能否由当前 aggregate 区分？`YES`。
3. 是否有跨 provider/actor/encounter/rate dimension 稳定胜出的无参数 candidate？`NO`。
4. percentage × rate ownership：`Not determined`。
5. nominal expiry 如何替换？使用上述 packet-aware lifecycle，nominal 只触发 PendingRemoveGrace，remove/refresh/reset 优先，2 秒后 ExpiredFallback。
6. 是否得到 causal、bounded、missing-remove-safe fallback？`YES`，individual 2 秒 strategy。
7. Oracle 与 live causal fallback 差多少？完整 rate-overlap 平均 absolute prediction gap 0.557/constraint；clean core 0；窗口只多纳入 3 个 Technical events / 26,529 damage，无漏纳入。
8. Status fix 后：current ownership rate-overlap MAE 13,000.347 → 14,893.028（暴露原 ownership over-attribution）；固定 SharedBaseLog 时 4,271.336 → 2,837.615；final-100 rDPS MAE 364.088 → 327.076。
9. 是否满足进入 guaranteed equation identification 前提？`NO`。ownership 未唯一确认、仍有结构 residual，且关键 DH composition 稀疏。

## Generated artifacts

主报告与机器可读明细位于 `artifacts/fflogs-parity-harness/reports/`：

- `percentage-identification-summary.md`
- `percentage-identification-report.json`
- `percentage-direct-normal-core.csv`
- `percentage-residual-feature-analysis.csv`
- `percentage-cross-provider-validation.csv`
- `percentage-ownership-identifiability.csv`
- `percentage-ownership-discriminators.csv`
- `percentage-matched-interaction-controls.csv`
- `percentage-status-window-metrics.csv`
- `percentage-status-contribution-metrics.csv`
