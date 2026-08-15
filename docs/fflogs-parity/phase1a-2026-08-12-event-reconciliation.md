# Phase 1A Event Reconciliation — Red Hot and Deep Blue

本报告对应 encounter `f099db96-be70-4ac2-99a7-8b0ca1ea170f`、territory `1322`。Phase 1A 只增强开发者诊断；没有修改 `RaidDpsEstimator`、正式 DamageMetricDuration、用户可见 DPS/rDPS、Overlay 或 Host。

FFLogs 参考仍来自用户截图中的 report `nLM8q6TFA2kfVyc3` / `fight=last`。该截图只显示 `78.93M`，没有 exact event export，因此本报告把“本地观察”“官方语义”“推断候选”和“未知”严格分开。

## 结论摘要

- DACT `80,461,274` 可以精确守恒为 `76,449,628` direct ActionEffect + `4,011,646` normalized periodic damage。
- Phase 0 的 `friendly-fire candidate = 165,198` 应改名为 `party-target periodic candidate`。它是 8 个正值 raw `24` tick；历史文件没有 normalized target rows，不能把它直接认定为最终 DACT outgoing 或 FFLogs exclusion。
- `Parser normalization = +251,705` 已按 raw target bucket 拆为：normalized periodic 相对 party-source raw Boss periodic 增加 `416,903`；provisional raw total 还包含 `165,198` party-target periodic，所以 layer-total 净值严格为 `416,903 - 165,198 = 251,705`。这是守恒分解，不宣称两项就是 parser 的逐事件加/减规则。
- 两个 SMN 的 47 个 summon entity instance 被合并到 owner，但 raw direct source damage 与 owner 聚合后 DACT direct damage逐 actor完全相等；没有 owner + pet 双计。
- fight 范围内 2,053 个 party-owned direct events 只命中 `炽红`、`深蓝`。没有 trash/add/non-fight/environment/friendly direct target 被加入最终 outgoing total。
- 唯一 packet-HP overkill candidate 是 `21:31:42.388`、packet `00007CA8`、`笙夜之歌` 的 `团契` 对 `炽红`：raw `120,259`，packet 前 HP `91,744`，candidate `28,515`。FFLogs 官方 calculated-damage schema 明确 `amount` 不包含 overkill；是否该 event 在此 fight 的 exact amount 为 `91,744` 仍需 exact export 验证。
- 两 Boss 的 max HP 都是 `39,480,861`，合计 `78,961,722`。`78,961,722 - 28,515 = 78,933,207`，落在截图 `78.93M` 的显示区间内。这个一致性很强，但“兄弟同心/shared HP 如何被 FFLogs 处理”没有公开的 encounter-specific event rule，因此只能标为 `inferred`。

## Total Damage 与候选差异

| 项目 | Amount | Evidence status | 说明 |
|---|---:|---|---|
| DACT saved total | 80,461,274 | observed | saved encounter exact total |
| FFLogs screenshot interval | 78,925,000 .. 78,935,000 | observed | `78.93M` 按 0.01M 显示精度形成的区间 |
| DACT - screenshot delta | 1,526,274 .. 1,536,274 | observed/bounded | exact reference total 尚缺失 |
| Combined boss max HP | 78,961,722 | observed | `39,480,861 × 2` |
| Final packet overkill candidate | 28,515 | observed local + official semantic | packet HP 可计算；FFLogs amount 排除 overkill 是公开 schema 语义 |
| Combined-HP consistency reference | 78,933,207 | inferred | `combined max HP - local overkill candidate`；不是已确认 FFLogs 公式 |
| Candidate total delta | 1,528,067 | inferred | `DACT - 78,933,207` |
| Numerical residual vs screenshot | -1,793 .. +8,207 | bounded/inferred | 前提是 combined-HP candidate 正确；绝对上限约 raid total 的 `0.0104%` |

数值残差已经低于 `0.2%` 目标，但证据残差并没有消失：其中 `1,499,552`（DACT total 相对 combined max HP 的超额）仍无法逐条指认为 FFLogs exclusion。它可能涉及 shared-HP/equalization、target state 或 encounter-specific effective damage semantics。没有 exact FFLogs events 时，不能把数值吻合升级为正式排除规则。

### Delta breakdown（不可混加）

| Category | Amount | Evidence status | 是否可作为 FFLogs delta 加项 |
|---|---:|---|---|
| Shared-HP / equalization consistency candidate | 1,499,552 | inferred | 是候选；缺 exact events，不能写正式 rule |
| Final packet overkill candidate | 28,515 | observed local / official general semantic | 是候选；缺 exact reference event amount |
| Candidate explained total | 1,528,067 | inferred | 上两项之和 |
| Unknown numerical residual | -1,793 .. +8,207 | bounded/inferred | screenshot rounding 下的剩余区间 |
| Parser normalization | +251,705 | observed layer relationship | 否；它解释 DACT total 如何形成，已经包含在 80,461,274 内 |
| Party-target raw periodic | 165,198 | observed raw / normalized unknown | 否；它是 parser-normalization 分解中的 raw bucket，不是已证明的 FFLogs exclusion |

如果只计算“已由 exact FFLogs events 证明”的 delta，目前仍不能消除 `1,526,274 .. 1,536,274`：截图没有逐事件证据。`<0.011%` 是强一致性假设下的 numerical residual，不是已确认 parity residual。

## Raw → normalized → ledger 守恒

| Layer / category | Damage | Events | Evidence status |
|---|---:|---:|---|
| Party-owned direct `21/22` to bosses | 76,449,628 | 2,053 | observed |
| Provisional party-source raw `24` to bosses | 3,594,743 | 243 positive | observed |
| Provisional party-source raw `24` to party targets | 165,198 | 8 positive / 13 rows | observed raw; normalized treatment unknown |
| Provisional party-owned raw total | 80,209,569 | 2,304 positive | observed |
| Normalized direct component | 76,449,628 | 2,053 | observed; saved total minus normalized periodic also matches every actor |
| Normalized periodic component | 4,011,646 | 627 hit rows | observed aggregate; historical MasterSwing rows were not persisted |
| Normalized / DACT total | 80,461,274 | 2,680 hits | observed |
| Parser normalization net | +251,705 | +376 hit rows | observed layer-total relationship |

Parser normalization 的可验证拆分：

```text
normalized party-attributed periodic                 4,011,646
- provisional party-source raw boss periodic        3,594,743
= actor/target reassignment or simulation delta       416,903

actor/target reassignment or simulation delta          416,903
- party-target rows present in provisional raw total   165,198
= parser normalization net                             251,705
```

raw 中还有 22 个命中 Boss、source 为 `E0000000` 或另一个 Boss 的 periodic ticks，共 `420,381`。它们与 `+416,903` 高度一致，是 source reassignment/simulation candidate；但 compiled FFXIV_ACT_Plugin 没有公开中间映射，历史 encounter 又没有保存 normalized MasterSwing，所以不能用相差仅 `3,478` 的数值吻合强行建立一对一配对。

### Normalized periodic actor aggregates

这些值由 saved actor total 减去 owner-resolved raw direct total得到，actor aggregate 是 exact local；不是伪造的 normalized event rows。

| Actor | Raw direct | Normalized periodic | Periodic hit rows | Saved total |
|---|---:|---:|---:|---:|
| 西卡利亚 | 14,685,334 | 174,431 | 54 | 14,859,765 |
| 笙夜之歌 | 11,648,123 | 0 | 28 | 11,648,123 |
| 唐酥饼 | 11,373,833 | 0 | 0 | 11,373,833 |
| 伊斑斑 | 9,637,966 | 113,275 | 43 | 9,751,241 |
| 黛朵洛·彼缪 | 9,456,980 | 216,684 | 82 | 9,673,664 |
| 埃斯蒂尼安丿 | 8,107,014 | 172,138 | 73 | 8,279,152 |
| Migi | 6,106,139 | 1,700,465 | 163 | 7,806,604 |
| 阕寒猫 | 5,434,239 | 1,634,653 | 184 | 7,068,892 |
| Total | 76,449,628 | 4,011,646 | 627 | 80,461,274 |

raw Boss periodic source 与 normalized actor periodic 的 aggregate 差如下。这里能确定 actor bucket 移动了多少，但不能确定是哪一条 raw tick 映射到哪一条 MasterSwing。

| Actor | Raw party-source Boss periodic | Normalized actor periodic | Delta |
|---|---:|---:|---:|
| 埃斯蒂尼安丿 | 1,041,826 | 172,138 | -869,688 |
| 伊斑斑 | 113,275 | 113,275 | 0 |
| 笙夜之歌 | 533,481 | 0 | -533,481 |
| 唐酥饼 | 0 | 0 | 0 |
| Migi | 594,856 | 1,700,465 | +1,105,609 |
| 黛朵洛·彼缪 | 548,318 | 216,684 | -331,634 |
| 西卡利亚 | 174,431 | 174,431 | 0 |
| 阕寒猫 | 588,556 | 1,634,653 | +1,046,097 |
| Total | 3,594,743 | 4,011,646 | +416,903 |

## Top parser-normalization raw evidence

下面是 22 个 raw-source 非 party、target 为 Boss 的 periodic events；总和 `420,381`。这些是 parser reassignment candidate，不是已确认 FFLogs exclusion。

| Timestamp | Raw source | Target | Status ID | Amount | Raw checksum |
|---|---|---|---|---:|---|
| 21:27:18.500 | E0000000 | 深蓝 | 0 | 22,629 | `594a1515247d6426` |
| 21:27:21.483 | E0000000 | 深蓝 | 0 | 13,226 | `b19fcbb92aa490b0` |
| 21:27:30.482 | E0000000 | 深蓝 | 0 | 17,581 | `1d91042023319571` |
| 21:27:33.468 | E0000000 | 深蓝 | 0 | 23,067 | `eaf45114d3eec3bc` |
| 21:29:45.464 | E0000000 | 深蓝 | 0 | 23,505 | `7ad9f6f0706007f8` |
| 21:29:48.495 | E0000000 | 深蓝 | 0 | 24,668 | `8112fb6382020ab3` |
| 21:29:51.479 | E0000000 | 深蓝 | 0 | 18,061 | `da392b6b84be24ac` |
| 21:29:54.466 | E0000000 | 深蓝 | 0 | 19,125 | `3bda45181815027e` |
| 21:29:57.493 | E0000000 | 深蓝 | 0 | 23,912 | `ff2369b321318a32` |
| 21:30:00.478 | E0000000 | 深蓝 | 0 | 18,289 | `123432fec679bc12` |
| 21:30:03.462 | E0000000 | 深蓝 | 0 | 23,804 | `33f0de347e11fb02` |
| 21:30:06.492 | E0000000 | 深蓝 | 0 | 8,274 | `a4bf7208ae6e580f` |
| 21:30:09.481 | E0000000 | 深蓝 | 0 | 8,172 | `cd4997b76e042bb8` |
| 21:30:12.462 | E0000000 | 深蓝 | 0 | 8,089 | `fd24b2da946ffc17` |
| 21:30:15.491 | E0000000 | 深蓝 | 0 | 8,906 | `12586452514a03a7` |
| 21:30:18.473 | E0000000 | 深蓝 | 0 | 8,173 | `950258c928a895fb` |
| 21:30:39.469 | E0000000 | 深蓝 | 0 | 27,838 | `5fb4063c0ff2abfb` |
| 21:30:42.503 | E0000000 | 深蓝 | 0 | 21,569 | `07102d56e6a9831b` |
| 21:31:39.489 | E0000000 | 炽红 | 0 | 28,313 | `113dadde2d9bf8c0` |
| 21:31:39.489 | 炽红 (`40018738`) | 深蓝 | 0 | 26,902 | `b79759f667d631ce` |
| 21:31:42.476 | 深蓝 (`40018739`) | 炽红 | 0 | 23,907 | `ee4e1c3417d8dc53` |
| 21:31:42.476 | E0000000 | 深蓝 | 0 | 22,371 | `8bcdeeac9fff2056` |

下面 8 个正值 party-target periodic events 总和 `165,198`。raw source 在不同队员之间跳变，status ID 均为 `0`，所以 Phase 0 的 “friendly fire” 命名过强；Phase 1A 将其改为 `party-target periodic candidate`。没有历史 normalized target rows，最终 treatment 标为 unknown。

| Timestamp | Raw source | Target | Amount | Raw checksum |
|---|---|---|---:|---|
| 21:26:04.927 | 笙夜之歌 | 笙夜之歌 | 23,096 | `164c9f9cce9bbf22` |
| 21:26:07.914 | Migi | 笙夜之歌 | 22,250 | `60b9ea0ff7144bb2` |
| 21:26:16.911 | 笙夜之歌 | 笙夜之歌 | 21,934 | `5b80899046e2a6fb` |
| 21:28:41.473 | 唐酥饼 | 西卡利亚 | 5,754 | `1e209f233de2af3e` |
| 21:28:44.458 | 唐酥饼 | 西卡利亚 | 23,285 | `e65859c791a34819` |
| 21:28:50.477 | 唐酥饼 | 西卡利亚 | 21,821 | `64aac76c106ce75b` |
| 21:31:34.942 | 西卡利亚 | 伊斑斑 | 24,282 | `a8cf9431ac8974c7` |
| 21:31:37.929 | 唐酥饼 | 伊斑斑 | 22,776 | `a2469407df027ea4` |

## Pet / owner reconciliation

| Owner | Pet direct damage | Pet events | Summoned entity instances | Owner-resolved direct | Saved direct component |
|---|---:|---:|---:|---:|---:|
| 西卡利亚 | 3,575,994 | 59 | 23 | 14,685,334 | 14,685,334 |
| 伊斑斑 | 2,254,193 | 53 | 24 | 9,637,966 | 9,637,966 |

检查结果：

- `raw checksum + effect index` duplicate groups: `0`
- `packet + target index + effect index + source + target + action` duplicate groups: `0`
- `timestamp + source + target + action + amount` duplicate groups: `0`
- owner attribution 前后 damage conservation: exact

结论是 observed：pet respawn/instance merge 存在，但没有证据显示 owner + pet 双计。新 paired replay fixture 还会在 one raw → two identical normalized rows时保留 `ambiguous` 和 `duplicate normalization candidate`，不会静默合并。

## Target scope

| Target | Party-owned direct | Provisional party-source periodic | Non-party raw periodic | Scope result |
|---|---:|---:|---:|---|
| 炽红 (`40018738`) | 45,035,304 | 2,064,825 | 52,220 | encounter boss |
| 深蓝 (`40018739`) | 31,414,324 | 1,529,918 | 368,161 | encounter boss |
| 笙夜之歌 | 0 | 67,280 | 0 | party-target periodic candidate; normalized treatment unknown |
| 西卡利亚 | 0 | 50,860 | 0 | party-target periodic candidate; normalized treatment unknown |
| 伊斑斑 | 0 | 47,058 | 0 | party-target periodic candidate; normalized treatment unknown |

没有 party-owned direct event 命中 adds、trash、permanently retired target 或 non-fight target。历史文件没有 normalized target split，因此 `4,011,646` periodic 在两个 Boss 间的最终分配仍需 MasterSwing 或 FFLogs exact events。

## Top actor delta evidence

下面仍是截图字段推导的 actor delta interval；是 inferred，不是 exact event attribution。

| Actor | DACT total | DACT - FFLogs interval |
|---|---:|---:|
| 唐酥饼 | 11,373,833 | +473,153 .. +474,574 |
| 笙夜之歌 | 11,648,123 | +309,668 .. +311,145 |
| 阕寒猫 | 7,068,892 | +282,837 .. +283,735 |
| 埃斯蒂尼安丿 | 8,279,152 | +153,077 .. +154,145 |
| 西卡利亚 | 14,859,765 | +108,745 .. +110,656 |
| 黛朵洛·彼缪 | 9,673,664 | +106,243 .. +107,494 |
| 伊斑斑 | 9,751,241 | +64,743 .. +66,010 |
| Migi | 7,806,604 | +27,684 .. +28,709 |

Top 50 positive DACT-vs-reference events、Top abilities delta 和 exact target delta 无法对这场历史 fight 生成：缺失 FFLogs exact event export，且 Phase 0 recorder 部署前的 normalized MasterSwing 没有持久化。新 Phase 1A report 对未来/重新录制的 paired fixture 已支持这些视图；本报告没有用弱时间/金额匹配伪造历史 events。

## Phase 1A infrastructure

- canonical event 增加真实 `packetId`、`parserEventId`、raw checksum、target/effect index/type、overkill/absorbed 字段。
- ACT `TimeSorter` 不再冒充 packet ID；只有 parser tag 真正提供 packet ID 时才写入该字段。
- correlation 状态：`matched`、`ambiguous`、`intentionally ignored`、`unmatched raw`、`unmatched normalized`、`unmatched reference`。
- evidence 状态：`official`、`observed`、`inferred`、`unknown`。
- 每条 reconciliation row 包含 timestamp、source/owner/target/ability ID 与名称、raw/normalized/included/reference amount、category、reason 和三层 ID。
- report 输出 Top 50 positive reference delta、unmatched normalized/raw，以及 actor/ability/target delta aggregates。
- `ParityReferenceFight` 支持 JSON/CSV 开发者导入；正常运行不依赖 FFLogs API，也不会覆盖本地结果。
- paired fixture 覆盖 strict packet correlation、pet respawn、duplicate owner attribution candidate、friendly target、overkill、environment、multiple targets、trash+boss、DoT one-to-many、large packet、unmatched raw/normalized。
- replay 断言 raw、normalized ledger 和 owner attribution 三组守恒。

## 仍缺失的证据

1. 同 fight 的 FFLogs exact damage event JSON/CSV；这是把 `1,499,552` shared-HP candidate 分配到具体 event/target/ability 的必要条件。
2. 历史 normalized MasterSwing 明细；closed-source compiled parser 的 DoT source reassignment 无法从 raw `24` 唯一逆推。
3. FFLogs 对该 encounter 的公开 shared-HP/equalization rule。公开 schema 解释 effective damage/overkill，但不证明此 encounter 的 target-state 筛选。

因此 Phase 1A 不新增任何正式 exclusion rule。Phase 1B 应先用 exact reference events 验证 shared-HP/target-state 假设，再提出 EncounterTimeline/downtime/fight-boundary 的正式变更；本阶段不进入 Phase 1B。

公开资料：[FFLogs CalculatedDamageEvent schema](https://www.fflogs.com/scripting-api-docs/ff/interfaces/RpgLogs.CalculatedDamageEvent.html)、[FFLogs expression language damage definitions](https://www.fflogs.com/help/pins)、[FFXIV_ACT_Plugin repository notice](https://github.com/ravahn/FFXIV_ACT_Plugin)。
