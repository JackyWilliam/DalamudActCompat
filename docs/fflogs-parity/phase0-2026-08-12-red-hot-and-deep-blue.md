# Phase 0 Parity Report — Red Hot and Deep Blue

This report is a Phase 0 diagnostic for encounter `f099db96-be70-4ac2-99a7-8b0ca1ea170f` in territory `1322` (`AAC Heavyweight (M2)`). It does not change the production DPS/rDPS path.

FFLogs reference: report `nLM8q6TFA2kfVyc3`, `fight=last`, as shown in the supplied screenshot. The report was not accessible through unauthenticated public API credentials, so only visible rounded fields are treated as FFLogs evidence here.

Evidence levels used below:

- **Exact local**: read from the saved DACT encounter or decoded from the recorded `21/22/24/34` pipe lines.
- **Rounded FFLogs**: read from the supplied screenshot; an amount shown as `78.93m` has a ±5,000 display interval.
- **Inferred**: constrained by several displayed fields or proposed as a comparison category, but not proven without an exact FFLogs event export.

## Fight and duration clocks

| Clock / boundary | Value | Evidence |
|---|---:|---|
| DACT saved start | `2026-08-12T21:25:25.814+08:00` | Exact local; first party damage |
| Last party damage | `2026-08-12T21:31:43.637+08:00` | Exact local; raw action effect |
| Boss death | `2026-08-12T21:31:45.866+08:00` | Exact local; network death line |
| DACT saved end | `2026-08-12T21:31:53.7767521+08:00` | Exact local; duty/session completion |
| DACT saved wall duration | `387.963s` | Exact local; saved `Duration` |
| DACT damage-event wall | `377.823s` | Exact local; first-to-last party damage |
| DACT current union downtime | `94.745s` | Exact local; union of every damaged target's NameToggle-off interval |
| DACT current DamageMetricDuration | `283.078s` (`04:43.078`) | Exact local; current production value |
| All-targets-unavailable downtime | `26.646s` | Diagnostic candidate; intersection across both bosses |
| Candidate DamageMetricDuration | `351.177s` | Diagnostic only; no production behavior changed |
| FFLogs displayed fight duration | about `379.4s` | Rounded FFLogs screenshot |
| FFLogs amount/DPS divisor | `352.147–352.192s` | Inferred from total `78.93m` and `224,124.9 DPS` display rounding |

The `04:43` display is therefore not explained by an early encounter reset. The saved encounter spans the whole duty result, but the current production rate subtracts the **union** of target-specific untargetable windows. In this two-boss fight, that removes time when only one boss is unavailable and the other remains damageable.

## Downtime provenance

The phase labels below are observational labels. No FFLogs phase name is asserted from NameToggle alone.

| Observed phase | Target | Start | End | Seconds | Relationship |
|---|---|---|---|---:|---|
| observed-transition-1 | 炽红 (`40018738`) | `21:26:44.450` | `21:27:52.542` | 68.092 | Contains a shorter 深蓝 downtime |
| observed-transition-2 | 深蓝 (`40018739`) | `21:27:00.591` | `21:27:11.776` | 11.185 | Both targets unavailable; already inside transition-1 |
| observed-transition-3 | 炽红 (`40018738`) | `21:28:02.481` | `21:28:17.942` | 15.461 | Both targets unavailable |
| observed-transition-4 | 深蓝 (`40018739`) | `21:28:02.481` | `21:28:17.942` | 15.461 | Both targets unavailable |
| observed-transition-5 | 深蓝 (`40018739`) | `21:28:42.897` | `21:28:54.089` | 11.192 | 炽红 remains available |

Current union intervals are `68.092 + 15.461 + 11.192 = 94.745s`. Simultaneous all-target downtime is `11.185 + 15.461 = 26.646s`. The `68.092s` interval is counted once in the union, not once per target.

## DamageLedger evidence

The historical encounter predates the Phase 0 MasterSwing recorder, so its saved event arrays are empty. Packet-level reconstruction can still expose the raw categories, while the saved combatant totals provide the exact current normalized total.

| Layer / category | Damage | Positive events | Precision | Notes |
|---|---:|---:|---|---|
| Raw `21/22` damage effects | 76,449,628 | 2,053 | Exact local | Decoded with FFXIV_ACT_Plugin's `Value + Flags1 × 65536` large-value rule |
| Raw `24` DoT to bosses | 3,594,743 | 237 | Exact local | Ownership is provisional before parser DoT simulation/redirect |
| Raw `24` party-target damage | 165,198 | 14 | Exact local | Friendly-fire comparison candidate |
| Raw party-owned total | 80,209,569 | 2,304 | Exact local | Before compiled parser source redirect and DoT simulation |
| ACT normalized / DACT saved total | 80,461,274 | 2,680 hits | Exact local | Current production DamageLedger source |
| Parser normalization delta | +251,705 | +376 hits | Exact local | Normalized total minus provisional raw total; not an FFLogs exclusion claim |

Raw direct effects contain 522 critical hits, 460 direct hits, and 118 critical-direct hits. The saved normalized encounter contains 632 critical hits and 128 critical-direct hits; it did not persist total direct hits. New Phase 0 live diagnostics persist all four counters from normalized MasterSwing events.

## Per actor comparison

FFLogs exact actor amounts are unavailable. The delta bounds below combine the screenshot's rounded Amount/DPS fields with the common divisor interval above; they are inferred, not exact event attribution.

| Actor | Job | DACT damage | DACT hits | Crit | Crit+Direct | FFLogs Active | DACT − FFLogs damage interval |
|---|---|---:|---:|---:|---:|---:|---:|
| 西卡利亚 | SMN | 14,859,765 | 299 | 79 | 29 | 89.33% | +108,745 … +110,656 |
| 笙夜之歌 | RPR | 11,648,123 | 322 | 53 | 15 | 91.19% | +309,668 … +311,145 |
| 唐酥饼 | DNC | 11,373,833 | 307 | 98 | 28 | 88.69% | +473,153 … +474,574 |
| 伊斑斑 | SMN | 9,751,241 | 329 | 64 | 17 | 89.98% | +64,743 … +66,010 |
| 黛朵洛·彼缪 | PLD | 9,673,664 | 423 | 101 | 1 | 91.34% | +106,243 … +107,494 |
| 埃斯蒂尼安丿 | PLD | 8,279,152 | 398 | 111 | 26 | 91.14% | +153,077 … +154,145 |
| Migi | SGE | 7,806,604 | 284 | 73 | 12 | 89.75% | +27,684 … +28,709 |
| 阕寒猫 | WHM | 7,068,892 | 318 | 53 | 0 | 89.51% | +282,837 … +283,735 |

DACT `ActorActiveTime` cannot be reconstructed honestly from the historical aggregate file. FFLogs Active percentages are retained as reference values only. The live Phase 0 recorder reports an explicitly labelled first-to-last included-damage span until FFLogs' active-time state machine can be verified.

## 1.9% total-damage delta breakdown

| Step | Amount | Status |
|---|---:|---|
| DACT saved total | 80,461,274 | Exact local |
| FFLogs displayed total center | 78,930,000 | Rounded FFLogs |
| Total delta | +1,526,274 … +1,536,274 (`1.934% … 1.946%`) | Bounded by FFLogs display rounding |
| Local parser normalization adjustment | 251,705 | Exact local relationship; whether FFLogs accepts the corresponding events must be checked event-by-event |
| Raw friendly-fire candidate | 165,198 | Exact local category; FFLogs treatment not proven from the screenshot |
| Packet-HP estimated overkill candidate | 28,515 | Inferred candidate; FFLogs calculated damage excludes overkill, but packet pairing must be verified |
| Residual after the three local candidates | 1,080,856 … 1,090,856 | Unknown until an exact FFLogs event export is compared |

The table deliberately does **not** claim that the three candidates are FFLogs exclusions. It proves where the local total is formed and leaves a bounded residual. An FFLogs events/CSV export for the same fight is required to turn the residual into exact ability/target/packet categories; the normal calculation path will not call FFLogs for these values.

## Phase 1 modification candidates

1. Replace the current any-target untargetable union with an encounter timeline policy that can express multi-target availability. The report already shows the competing `94.745s` and `26.646s` measurements; selection requires FFLogs event validation.
2. Define fight end from verified encounter/boss state rather than the duty completion tail, while preserving wipe/kill/reset evidence separately.
3. Pair raw ActionEffect packet IDs with normalized MasterSwing source redirects and DoT simulation so the `+251,705` parser adjustment is event-addressable.
4. Add explicit friendly-fire, overkill, Limit Break, environment, and encounter-target policies to `DamageLedger`; keep every exclusion reason in the report.
5. Only after duration and total damage meet thresholds, implement buff timelines and rDPS/aDPS/nDPS attribution in separate deterministic calculators.

Public behavior references used for interpretation: [FFLogs calculated damage event schema](https://www.fflogs.com/scripting-api-docs/ff/interfaces/RpgLogs.CalculatedDamageEvent.html), [FFLogs fight schema](https://www.fflogs.com/v2-api-docs/ff/reportfight.doc.html), and [FFLogs rDPS/aDPS definitions](https://www.fflogs.com/help/rdps).
