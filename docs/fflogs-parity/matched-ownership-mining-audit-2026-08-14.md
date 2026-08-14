# FFLogs same-actor ownership mining audit — 2026-08-14

## Outcome

Ownership status: **Not determined**.

The targeted longitudinal corpus adds useful evidence but does not satisfy the production gate:

- `SharedBaseLog` has the lowest pair-residual-shift MAE in every non-empty aggregate scope.
- The four clean normal-direct pairs split by actor: two pairs for `深眸似海蓝` favor `SharedBaseLog`, while two pairs for `弥生千鹤` favor `SharedShapley3`.
- There are no Grade-A same-encounter pairs and no DH-only matched component pairs.
- Crit+DH evidence is sufficient as a dimension sample: six A/B component pairs, including four clean normal-direct pairs across two VPR actors.
- `SharedBaseLog` and `SharedShapley3` make observably different aggregate predictions, but the mined observations do not identify one universal winner.

No production ownership, guaranteed-hit equation, prior/clamp, Life Surge, status fallback, or FFLogs reference was changed.

## Method

The miner is exposed as:

```text
--matched-ownership-mining
--matched-ownership-replay
```

It reuses the existing OAuth client, raw-response cache, retry/rate-limit handling, event normalizer, packet correlation, production two-second causal status lifecycle, and ownership decomposition. It does not rebuild the old harness or recompute the 1,738-row horizontal analysis.

Selection runs in two stages:

1. Scan cached/public ranking rows for VPR, PCT, MCH, WAR, SAM, and DNC in partition 9.
2. Preflight only same-actor groups with report metadata and DamageDone `taken[]`; download complete events only when a fight adds a rate dimension and forms a longitudinal contrast.

Every downloaded fight stores `WhyUseful`. Full events include `combatantinfo` when FFLogs emits it, and the unmodified API JSON is cached locally.

Candidate predictions are computed from causal replay before the FFLogs percentage aggregate is subtracted. The primary unit is a fixed percentage component:

```text
same canonical damage actor
+ same percentage buff status/provider job
+ different fight rate exposure
```

The reported residual shift is:

```text
(candidate prediction - FFLogs reference)_B
- (candidate prediction - FFLogs reference)_A
```

This keeps the percentage buff identity fixed instead of mixing unrelated provider aggregates.

## A. Mining Summary

| Metric | Count |
|---|---:|
| API candidates scanned | 6,616 |
| Ranking pages read | 72 |
| Metadata report/fight preflights | 245 |
| Unique cached API responses created by the miner | 396 |
| Imported existing ranking/matrix cache reads | 19 |
| Cache hits in the final mining pass | 364 |
| New network API requests in the final reproducible pass | 0 |
| Full fights fetched/replayed | 18 |
| Matched actors | 5 |
| Grade A component pairs | 0 |
| Grade B component pairs | 11 |
| Grade C component pairs | 2 |

The 245 preflights are not 245 downloaded event streams. They are cheap rejection checks over 60 longitudinal actor groups. Only 18 fights passed the fixed-percentage, identity, rate-dimension, and matched-group requirements.

## B. Actor Summary

| Actor | Job | World | FFLogs canonicalID | Partition | Fights | Exposure coverage | Best grade |
|---|---|---|---:|---:|---:|---|---|
| 希奈斯特拉 | PCT | 幻影群岛 | 19060717 | 9 | 3 | No-rate, Crit-only, Crit+DH | C |
| 弥生千鹤 | VPR | 水晶塔 | 20801974 | 9 | 4 | No-rate, Crit-only, Crit+DH, multiple Crit | B |
| 深眸似海蓝 | VPR | 静语庄园 | 20928322 | 9 | 3 | Crit-only, Crit+DH, multiple Crit | B |
| 甜心小孔 | SAM | 琥珀原 | 20581308 | 9 | 4 | No-rate, Crit-only, Crit+DH, multiple Crit | B |
| 髙松燈 | MCH | 拉诺西亚 | 20676663 | 9 | 4 | No-rate, Crit-only | B |

All five selected actors resolved through `Character.canonicalID`. Name, numeric server ID, world, region, job, and partition were retained and the report actor world was independently verified. Name-only matches were not accepted.

Every selected fight emitted a cached combatant-info packet with `gear[]`. The miner preserves that raw evidence but does not manufacture item level, Cu, or Du values that FFLogs did not expose as a stable aggregate field.

## Match Quality

The diagnostic score is provenance only; it is never an ownership weight:

- 50: canonical identity plus report-world verification.
- 20: same encounter.
- 10: same report.
- 10: identical active percentage composition.
- 10/5: within 14/45 days.
- 5: both components are clean direct-normal.

Grade A requires same encounter, identical percentage composition, and score at least 85. Grade B requires score at least 60. Grade C is auxiliary.

The miner also queried other kills in selected reports to seek same-report/same-encounter controls. None had the same actor present with a comparable percentage component and a changed rate dimension, so no Grade-A pair was created.

## C. Matched Groups

The table lists the 11 Grade-B provider-component pairs. Values are candidate residual shifts in damage; lower absolute value wins.

| Actor | Fixed percentage component | Fight A | Fight B | Rate change | Current | BaseLog | Shapley | Shapley3 | Winner | Evidence |
|---|---|---|---|---|---:|---:|---:|---:|---|---|
| 弥生千鹤 | DNC Technical Finish | QZLN8PBw9FnTjXCb:12 | hzGKgx2fnyLPpTXH:26 | No-rate → Crit+DH | 33,693.1 | 2,251.2 | 2,755.3 | 2,458.8 | BaseLog | guaranteed/periodic mixed |
| 深眸似海蓝 | DNC Technical Finish | VRA9K3ktL4pFay8G:5 | gGkXVfNFRcbZLhYm:1 | Crit+DH → Crit-only | -31,431.4 | -1,595.1 | -2,592.2 | -2,219.8 | BaseLog | normal-direct |
| 深眸似海蓝 | DNC Technical Finish | VRA9K3ktL4pFay8G:5 | 7BPRVHbLJAxk4McF:1 | Crit+DH → Crit-only | -31,044.4 | -1,595.9 | -2,590.6 | -2,218.1 | BaseLog | normal-direct |
| 甜心小孔 | NIN Dokumori | pcZ6mntRwWDY2Khz:2 | fhdXCbxaGPVMYv19:3 | Crit-only → Crit+DH | 29,958.4 | 3,424.2 | 3,581.9 | 3,240.8 | Shapley3 | guaranteed mixed |
| 弥生千鹤 | DNC Technical Finish | P2tkCNZpRzADwjLr:19 | hzGKgx2fnyLPpTXH:26 | Crit-only → Crit+DH | 21,810.6 | 1,393.1 | 1,689.3 | 1,392.8 | Shapley3 | normal-direct |
| 弥生千鹤 | DNC Technical Finish | KCz4QR7p1mq3kwrg:5 | P2tkCNZpRzADwjLr:19 | Crit+DH → Crit-only | -14,520.6 | -1,485.7 | -1,478.7 | -1,258.5 | Shapley3 | normal-direct |
| 甜心小孔 | SMN Searing Light | BxKpkCmbTGYw8cgM:11 | XctRPg9KJYLmbnHB:1 | Crit-only → No-rate | -8,506.6 | -1,231.2 | -1,274.8 | -1,274.8 | BaseLog | guaranteed mixed |
| 髙松燈 | NIN Dokumori | 7WZAg36QxPYVGh1t:2 | GN7LcWHwna3bBDqd:13 | No-rate → Crit-only | 6,753.8 | 173.9 | 226.5 | 226.5 | BaseLog | guaranteed mixed |
| 髙松燈 | NIN Dokumori | VdD8qNaHX14YWB2G:5 | GN7LcWHwna3bBDqd:13 | No-rate → Crit-only | 19,924.5 | 13,344.5 | 13,397.1 | 13,397.1 | RateFirst | guaranteed mixed |
| 髙松燈 | NIN Dokumori | YdXyGDtk7ZpCF1Pv:3 | 7WZAg36QxPYVGh1t:2 | Crit-only → No-rate | -6,140.1 | -868.1 | -911.2 | -911.2 | BaseLog | guaranteed mixed |
| 髙松燈 | NIN Dokumori | YdXyGDtk7ZpCF1Pv:3 | VdD8qNaHX14YWB2G:5 | Crit-only → No-rate | -19,310.7 | -14,038.7 | -14,081.8 | -14,081.8 | RateFirst | guaranteed mixed |

The four primary normal-direct controls are the two VPR actors. Their winner flips by actor:

```text
深眸似海蓝: SharedBaseLog 2 / 2
弥生千鹤:   SharedShapley3 2 / 2
```

This is more important than the small overall MAE edge. It rejects promotion of either candidate as a universal ownership rule.

## D. DH Evidence

**NO**.

There are zero A/B same-actor percentage-component pairs involving a pure DH-only rate dimension. Extending preflight from 24 to 60 longitudinal actor groups still found none that passed the fixed-percentage and identity gates.

Partition 9 makes this target intrinsically rare: BRD supplies the major party DH buff but its song cycle also supplies small Crit/DH rate effects, so a fight-wide BRD exposure commonly becomes Crit+DH. A useful future control must prove that the fixed percentage component overlaps Battle Voice/Army's Paeon but not Wanderer's Minuet, Chain Stratagem, Battle Litany, or Devilment.

## E. Crit+DH Evidence

**YES**, for the dimension sample; **NO**, for final ownership confirmation.

There are six A/B Crit+DH component pairs. Four are clean direct-normal controls across two canonical VPR actors. This is enough to test the dimension without the hidden guaranteed equation, but the two actors select different candidates and every pair is Grade B.

## F. Candidate Ranking

MAE is calculated over matched provider-component residual shifts, not over the old 1,738-row corpus.

| Scope | N | Current | RateFirst | BaseLog | Shapley | Shapley3 | Winner |
|---|---:|---:|---:|---:|---:|---:|---|
| A-grade matched | 0 | — | — | — | — | — | N/A |
| A+B matched | 11 | 20,281.298 | 15,034.621 | **3,763.786** | 4,052.673 | 3,880.016 | BaseLog |
| normal-direct | 4 | 24,701.764 | 20,526.364 | **1,517.446** | 2,087.700 | 1,772.286 | BaseLog |
| Crit-only | 10 | 18,940.118 | 13,719.837 | **3,915.047** | 4,182.409 | 4,022.141 | BaseLog |
| DH-only | 0 | — | — | — | — | — | N/A |
| Crit+DH | 6 | 27,076.428 | 22,180.432 | **1,957.527** | 2,447.998 | 2,131.459 | BaseLog |
| multiple provider | 6 | 27,076.428 | 22,180.432 | **1,957.527** | 2,447.998 | 2,131.459 | BaseLog |
| all | 13 | 20,106.139 | 15,111.160 | **3,431.029** | 3,706.927 | 3,543.893 | BaseLog |

`SharedBaseLog` wins the aggregate scopes, but the normal-direct actor-level flip remains. Overall MAE therefore cannot pass the acceptance gate.

## G. Identifiability and Ownership Status

`SharedBaseLog` versus `SharedShapley3` aggregate predictions are observationally distinguishable: **YES**. At least one A/B pair exceeds the 0.05-damage display tolerance by a wide margin.

Whether the mined evidence identifies the actual FFLogs rule: **NO**.

Reasons:

- zero Grade-A controls;
- zero DH-only controls;
- normal-direct winner flips across two canonical VPR actors;
- B-grade pairs still differ by encounter and rotation;
- guaranteed recipient aggregates cannot always separate ownership from the paused guaranteed equation.

Final ownership status: **Not determined**.

## H. Minimum Next Gap

The smallest decisive addition is:

```text
2 canonical VPR actors
same actor + same encounter + same partition
same fixed percentage component (prefer Technical Finish or Divination)
normal-direct only

Fight A: percentage + DH-only
Fight B: same percentage + Crit+DH

the only intended change is the added Crit dimension
```

This produces the largest useful `SharedBaseLog` versus `SharedShapley3` fork without invoking guaranteed-hit math. It is a precise missing pair, not a request for a larger random corpus.

## Artifacts

- `artifacts/fflogs-parity-harness/reports/matched-ownership-report.json`
- `artifacts/fflogs-parity-harness/reports/matched-ownership-fights.csv`
- `artifacts/fflogs-parity-harness/reports/matched-ownership-pairs.csv`
- `artifacts/fflogs-parity-harness/reports/matched-ownership-rankings.csv`
- `artifacts/fflogs-parity-harness/reports/matched-ownership-mining-2026-08-14.md`

The JSON contains every fight's `WhyUseful`, canonical identity provenance, candidate prediction before reference comparison, combatant-info availability, all component pairs, and all ranking statistics.
