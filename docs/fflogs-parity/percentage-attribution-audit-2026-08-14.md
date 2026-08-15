# FFLogs fixed-percentage attribution audit

Date: 2026-08-14

Scope: existing 106-fight cache; 2,217 fixed-magnitude provider-to-recipient constraints

Guaranteed Crit/DH/CDH equation work: paused

## Executive conclusion

The original percentage-control failure is not one common log-weight formula bug. It is a composite of two confirmed production metadata gaps, one confirmed harness packet-order loss, unresolved status-expiry boundaries, and a dominant rate-overlap structure that prevents the mixed 2,217-row aggregate from serving as a pure percentage control.

The published fixed-percentage formula is validated by the clean direct controls. With packet ordering and explicit removal, all 8 single-buff controls are exact, and 22 of 24 clean multi-buff controls are exact. The remaining full aggregate is not at component parity, so the acceptance answer for restarting guaranteed-equation identification is **NO**.

## Reference extraction audit

- FFLogs `DamageDone` recipient `taken[]` is the reference for provider -> recipient -> buff contribution.
- All 463 provider/fight `given[]` totals equal the sum of mapped recipient `taken[]` totals within 0.05 damage.
- Unmatched known fixed-percentage recipient references: 0.
- Provider actor, recipient actor, target-side debuff, party aura, single-target/partner scope, and pet-to-owner routing are resolved from cached actor and status identities before comparison.
- Self-sourced effects are excluded from rDPS credit. They were tested separately only as denominator diagnostics.
- FFLogs public data supplies no per-event or per-window percentage contribution. Window rows therefore contain forward-calculated DACT values and explicitly mark FFLogs window truth unavailable; no fight aggregate was back-solved into fake event truth.

This establishes that `given[]` and `taken[]` are mapped consistently. It does not prove that FFLogs exposes an independently calculated percentage component when Crit/DH-rate effects overlap.

## Public percentage math

For active external fixed multipliers `m_i`:

```text
M = product(m_i)
N_without_external_percentage = N / M
L = N - N / M
provider_i = L * log(m_i) / log(M)
```

The production multiplication, total removal, and log-weight allocation have this structure. Self multipliers are not part of the external `M`. Treating FFLogs calculated event `multiplier` as `M` is invalid: it also contains effects outside the documented external-percentage set and produced MAE 13,453.796.

## Confirmed defects and minimal fixes

### 1. AST card magnitude metadata

The official PvE job guide defines role-conditional values:

- The Balance: 6% for melee/tank targets, 3% otherwise.
- The Spear: 6% for ranged/healer targets, 3% otherwise.

Production previously used 6% for every target. `RaidDpsEstimator` now records the AddCombatant job ID, follows pet ownership to the owner job, and resolves the status multiplier by target role. If an external parser omits job metadata, the old 6% behavior is retained as the compatibility fallback rather than guessing a role.

Across 86 AST constraints, the authoritative role metadata changes MAE from 19,006.843 to 18,203.696. The remaining AST residual is not evidence that 6%/3% is wrong: all 45 Balance constraints and 37 of 41 Spear constraints overlap a rate buff, and there is no clean Balance control in this corpus.

### 2. Mage's Ballad coverage

The official Bard guide defines Mage's Ballad as a fixed 1% party damage effect. Production had no `0x8A9` entry. It is now covered at 1%.

| State | N | Mean residual | MAE | Max abs | Exact |
|---|---:|---:|---:|---:|---:|
| Before | 49 | -38,370.141 | 38,397.852 | 74,721.554 | 0 |
| After, production nominal expiry | 49 | -615.975 | 636.050 | 5,451.406 | 10 |
| After, observed event state | 49 | +49.009 | 49.633 | 459.740 | 37 |

For the 42 no-rate Mage's Ballad constraints, observed event-state mean/MAE are +14.080/+14.808 damage. The clean controls are exact to floating-point precision.

### 3. Packet correlation ordering in the harness

Direct damage takes its timestamp from the packet-matched `calculateddamage` event. The adaptor previously retained the later `damage` row's sequence, which could move the hit across an apply/remove event at the same millisecond. `AttributionSequence` now preserves the calculated event order and every production replay/probe consumes that order.

A concrete calibration case was `rx3vL2Wz9T4CMyAZ:19`: Ogi Namikiri's `calculateddamage` precedes the same-timestamp Devilment application, while the later `damage` row follows it. The corrected replay assigns zero Devilment contribution. The guaranteed diagnostic again reproduces production at floating-point scale (1,448 events; maximum event residual `9.82e-11`, fight residual `4.66e-10`).

## Single-buff controls

The strict clean control excludes rate overlap, guaranteed-hit classification, periodic damage, and pet damage.

| Timeline | N | Exact | Mean residual | MAE | Max abs |
|---|---:|---:|---:|---:|---:|
| Production nominal expiry | 8 | 2 | -2,205.247 | 2,205.247 | 6,376.905 |
| Packet order + explicit remove | 8 | 8 | ~0 | ~0 | `2.33e-10` |

Therefore the multiplier, damage basis, total removal, and provider allocation are correct for the simplest cases. Current production-duration windows are not fully at parity.

One proof case is `qnxrg4DdRhTXMzmF:19`: a 133,915-damage RDM event occurs 279 ms after the nominal Technical Finish end and 44 ms before the explicit remove. Its calculated multiplier confirms the effect was still active. Nominal expiry predicts 276,649.905 contribution; explicit removal predicts the FFLogs aggregate exactly at 283,026.810.

This does not justify changing production to always wait for remove. That counterfactual worsens the 2,217-row aggregate, so expiry behavior remains non-unique and was not patched.

## Multi-buff overlap

For the same strict direct/no-rate/no-guaranteed/no-pet control:

| Timeline | N | Exact | Mean residual | MAE | Max abs |
|---|---:|---:|---:|---:|---:|
| Production nominal expiry | 24 | 4 | -4,978.155 | 5,061.483 | 18,043.379 |
| Packet order + explicit remove | 24 | 22 | -656.236 | 656.236 | 12,953.227 |

The 22 exact rows prove multiplicative removal plus log-weight provider allocation for 2+ buffs. The two remaining rows are Technical Finish window outliers, not a provider split error common to overlap math.

## Residual localization

The decisive split is rate exposure:

| Cohort | N | Model | Mean residual | MAE | Exact |
|---|---:|---|---:|---:|---:|
| No external rate, direct | 111 | observed event state | -141.889 | 141.889 | 109 |
| No external rate, periodic | 368 | application snapshot | +530.519 | 1,500.888 | 165 |
| External Crit/DH rate overlap | 1,738 | observed event state | +14,775.045 | 14,892.471 | 0 |

Hit-time percentage attribution for DoTs is worse (overall MAE 12,598.776 versus 11,931.074 for application snapshot; no-rate periodic MAE 4,966.659 versus 1,500.888). Application snapshot is supported; hit-time DoT attribution is ruled out as the fix.

The 10 largest post-fix residuals are all rate-overlap rows. Examples include Divination -> SAM/MNK, Standard Finish -> SAM/VPR/MNK, Searing Light -> VPR, and Technical Finish -> MNK. Conversely, the closest controls include exact Searing Light, Technical Finish, and Mage's Ballad rows.

This means the remaining positive structure starts at the boundary between percentage attribution and rate-affected damage/reference decomposition. It is not evidence for a common fixed-percentage multiplier or log-allocation error. Because the public API has no event-local component truth, this audit cannot yet distinguish:

1. FFLogs component ordering/basis when rate effects are active;
2. an unobserved event-state input correlated with raid-burst windows;
3. residual periodic/window eligibility; or
4. a table-component semantic not represented by the public formula text.

## Provider/type results after the metadata fix

| Provider/type | N | Mean residual | MAE | Max abs | Interpretation |
|---|---:|---:|---:|---:|---|
| Mage's Ballad | 49 | -615.975 | 636.050 | 5,451.406 | metadata fix validated; expiry residual remains |
| Standard Finish partner | 108 | +34,485.227 | 35,122.171 | 67,776.428 | 107/108 rows have rate overlap |
| The Balance | 45 | +25,770.032 | 25,770.032 | 53,717.887 | 45/45 have rate overlap |
| Divination | 259 | +15,878.608 | 16,063.232 | 72,691.089 | no-rate observed MAE 1,562.221 |
| The Spear | 41 | +9,490.898 | 9,899.181 | 29,136.861 | four no-rate rows mean -825.140 |
| Party-wide | 1,890 | +7,554.990 | 9,369.255 | 72,691.089 | mixed rate/periodic state |
| Single-target | 86 | +18,009.049 | 18,203.696 | 53,717.887 | dominated by rate overlap |
| Enemy debuff | 133 | +1,400.211 | 6,870.895 | 36,250.332 | periodic/target-side residual remains |
| Partner/scoped | 108 | +34,485.227 | 35,122.171 | 67,776.428 | rate-overlap dominated |

The pattern is not “all providers fail the same way,” nor party-wide correct/single-target wrong. It is exposure-structure dependent.

## Full 2,217-constraint metrics

These pre/post rows use identical reference data and the corrected packet order; only the proven metadata changes differ.

| State | Mean | Median | MAE | RMSE | Max abs |
|---|---:|---:|---:|---:|---:|
| Before metadata fix | +8,098.290 | +4,621.351 | 11,682.623 | 17,974.565 | 74,721.554 |
| After metadata fix | +8,903.177 | +4,619.051 | 10,816.614 | 16,816.329 | 72,691.089 |

The mean becomes more positive because the missing Mage's Ballad rows were large negative residuals; MAE/RMSE/max all improve. This is not full component parity.

Self-active denominator alternatives were rejected. Including every self multiplier reduces aggregate MAE to 10,171.856 but reverses the sign, produces job-specific overcorrection, and conflicts with FFLogs' published definition of `M` as external percentage buffs. The FFLogs calculated multiplier diagnostic is also rejected.

## Original 100-fight replay

Raw damage remains 100/100 exact: 1,765,060,623 vs 1,765,060,623. Direct packet correlation remains 289,318/289,318, unmatched direct events 0, normalization warnings 0.

| State | Mean rDPS delta | Median | MAE | Max abs |
|---|---:|---:|---:|---:|
| Previous committed baseline | -350.070 | -340.540 | 356.952 | 1,151.696 |
| Metadata fix only | -351.011 | -340.540 | 355.936 | 1,151.696 |
| Metadata + corrected packet order | -359.325 | -350.242 | 364.088 | 1,151.696 |

The packet-order correction makes the final scalar metric worse while fixing a demonstrably wrong event ordering. It must not be reverted to regain cancellation. DNC rDPS remains unfrozen.

Technical + Standard mean component delta moved from approximately +107,140 damage in the previous report to +81,941 damage. Devilment mean component delta is now -188,098 damage. These opposing structures still cancel in the final scalar.

## Guaranteed-candidate diagnostic impact

No candidate equation changed. Recalculation after the percentage metadata and packet-order fixes gives:

| Candidate | Previous overall mean / MAE rDPS | Updated mean / MAE | Updated max abs |
|---|---:|---:|---:|
| CurrentProduction | -350.070 / 356.952 | -359.325 / 364.088 | 1,151.696 |
| ObservedHitRegular | -8.507 / 155.365 | -18.682 / 153.660 | 472.420 |
| UnscaledObservedHit | not retained / 159.836 | -106.393 / 163.900 | 576.445 |
| overlap-boundary diagnostic | -28.486 / 140.982 | -38.559 / 139.879 | 438.568 |

Thus roughly 10 rDPS of the previous candidate mean was percentage/event-order confounding, but the model ranking and `Not determined` status do not change. This round does not restart equation identification.

## Direct answers

1. **Root cause:** composite. Confirmed AST/Mage metadata gaps and calculateddamage ordering explain part; nominal/explicit window state explains the clean direct misses; the dominant remaining aggregate is rate-overlap-coupled and not identifiable as a public percentage formula defect.
2. **Single-buff parity:** yes under the event state supported by apply/remove ordering (8/8 exact); no under current nominal-expiry production (5/8 exact).
3. **Where multi-buff starts failing:** log allocation itself does not fail in clean controls (22/24 exact). The two remaining direct misses begin at Technical window eligibility; the broad failure begins when rate exposure/periodic structure is admitted.
4. **Layer classification:** reference extraction PASS; multiplier metadata FIXED for AST/Mage; damage basis/formula PASS in clean direct cases; packet ordering FIXED in harness; DoT snapshot supported; window expiry unresolved; provider allocation PASS in clean overlap; dominant mixed reference/component ordering unresolved.
5. **Common path or provider metadata:** not a single common formula bug. Two provider metadata bugs were fixed; remaining structure crosses providers but tracks rate exposure rather than provider type.
6. **Post-fix percentage result:** full MAE 10,816.614, but clean direct controls reach exact/floating-point parity.
7. **100-fight final MAE:** 356.952 -> 364.088 after all correctness changes; metadata alone gives 355.936.
8. **Guaranteed residual confound:** about 10 rDPS in candidate overall mean; insufficient to explain the hundreds-rDPS guaranteed residual or determine its equation.
9. **Ready to resume guaranteed equation identification:** **NO**. Mixed rate-overlap percentage reference semantics and production expiry behavior still have structured residuals.

## Minimum next discriminating experiment

Do not collect random fights. Use 5-10 existing-cache or targeted windows with the same provider/recipient actor and the same fixed percentage composition, split only by Crit/DH-rate exposure, while retaining calculated packet order and explicit status removal. The comparison must obtain an independent percentage component reference or an FFLogs scripting field that identifies active percentage status effects per calculated event. This distinguishes component-order/basis semantics from window eligibility without fitting a guaranteed equation.

## Authoritative sources

- FFLogs published rDPS math: <https://www.fflogs.com/help/rdps>
- FFLogs calculated damage event schema: <https://www.fflogs.com/scripting-api-docs/ff/interfaces/RpgLogs.CalculatedDamageEvent.html>
- FFXIV Astrologian Job Guide: <https://na.finalfantasyxiv.com/jobguide/astrologian/>
- FFXIV Bard Job Guide: <https://na.finalfantasyxiv.com/jobguide/bard/>

Generated machine-readable artifacts remain under `artifacts/fflogs-parity-harness/reports/` and are intentionally ignored by Git because they include public player names and large cached data.
