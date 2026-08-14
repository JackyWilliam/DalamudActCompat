# FFLogs percentage/rate component ordering audit

## Executive conclusion

The current percentage-first basis is incompatible with FFLogs `taken[]` whenever external Crit/DH-rate effects overlap, but the existing cache does **not** identify one exact replacement equation.

Two independent effects are proven:

1. **Component interaction ownership.** Production credits the complete percentage×rate interaction to the percentage provider. A rate-first counterfactual moves the complete interaction to the rate provider and over-corrects in the opposite direction. The parameter-free shared-order models are much closer, but are not component parity.
2. **Window eligibility.** Packet sequence plus explicit apply/refresh/remove state changes real eligible packets. Holding `SharedBaseLog` fixed, replacing nominal endpoints with observed packet state lowers rate-overlap MAE from `4,271.171` to `2,838.132` damage.

No production equation or expiry policy is changed in this round. The remaining signed structure is too large to call rounding, and the public cache does not contain the event-local FFLogs rate-effect totals needed to separate ordering from actual `Cu/Du` and rate-attribution error.

## Production pipeline

For one effective damage event, production executes:

```text
N_raw = EffectiveDamageEvent.Damage

external statuses = active owner/source/target statuses - self-sourced statuses

direct:
    percentage/rate state = hit-time state

periodic:
    percentage/rate state = application snapshot when a matching application exists

M = product(external percentage multipliers)
N_after_percentage = N_raw / M

percentage total = N_raw - N_after_percentage
provider i = percentage total * ln(mi) / ln(M)

Cu/Du = production HitBaseline immediately before the event

regular Crit basis     = N_after_percentage
regular DH basis       = N_after_percentage
simulated DoT basis    = N_after_percentage
guaranteed rate basis  = N_after_percentage

each transferred component:
    recipient adjustment -= contribution
    provider adjustment  += contribution
```

Therefore:

- `N_used_for_percentage_allocation = N_raw`;
- every Crit/DH path uses `N_raw / M`;
- production does **not** reuse full `N_raw` for rate contribution;
- production does **not** double-remove percentage damage;
- the multiplication interaction is assigned entirely to percentage by processing percentage first.

This agrees with the sequence described by the [FFLogs public rDPS page](https://www.fflogs.com/help/rdps), which defines removal of external percentage multipliers before its public regular Crit/DH calculation. That page does not document how the UI/API `taken[]` component breakdown owns the percentage×rate interaction, nor a guaranteed-hit branch.

## Event probe and CurrentProduction calibration boundary

The new probe replays production and reads its before/after `Percentage`, `Critical`, and `DirectHit` counters for every damage event. Those measured deltas are the authoritative CurrentProduction event result.

An independent timeline/math clone was also checked against those counters. It exactly reproduced:

| Component | Contributing events | Exact | MAE | Max abs |
|---|---:|---:|---:|---:|
| Percentage | 150,332 | 143,843 | 81.112 | 17,535.993 |
| Crit | 38,374 | 35,556 | 31.601 | 12,373.737 |
| DH | 11,065 | 8,327 | 50.223 | 12,397.447 |

The misses localize production-versus-normalized state semantics, especially death/refresh and unmatched periodic snapshots. The provider-level candidate table therefore names this path `CurrentProduction.TimelineClone`; it is a state diagnostic, not a perfect production oracle. The measured production counters remain authoritative in the event probe, but FFLogs does not expose provider-level event truth against which to rank those counters directly.

The event CSV contains 68,287 real percentage+rate overlap events with:

- report/fight/timestamp and `AttributionSequence`;
- damage owner, pet/source, target, action ID/name;
- Crit/DH/guaranteed flags;
- active percentage/Crit-rate/DH-rate providers;
- production percentage/Crit/DH deltas;
- percentage and rate bases;
- all ordering counterfactuals and conservation totals.

FFLogs has no cached per-event percentage truth. The event rows never back-solve the aggregate reference.

## Ordering counterfactuals

Let:

```text
N = effective damage
M = product of external fixed percentage multipliers
R = current-equation external rate contribution calculated on N/M
```

The tested no-fit models are:

### Percentage first

```text
P_pct_first = N - N/M
R_pct_first = R
```

### Rate first

Because the current rate equations are linear in their damage input:

```text
R_raw = M * R
N_after_rate = N - R_raw
P_rate_first = N_after_rate - N_after_rate/M
```

### Shared-order Shapley

```text
P_shapley = (P_pct_first + P_rate_first) / 2
R_shapley = (P_pct_first + R) - P_shapley
```

This is the two-order Shapley split, not a fitted `0.5` coefficient.

### Shared log

```text
B = N/M - R
Q = (N/M) / B
L = N - B

P_shared_log = L * ln(M) / ln(M*Q)
R_shared_log = L - P_shared_log
```

Percentage providers retain their ordinary `ln(mi)/ln(M)` split.

All four models conserve the same event total:

```text
P + R = N - B
```

They redistribute the interaction; they do not create or remove damage.

## Rate-overlap results

The same 1,738 provider/recipient/fight aggregate constraints are compared to FFLogs `taken[]`:

| Candidate | Mean | Median | MAE | RMSE | Max abs | - / 0 / + |
|---|---:|---:|---:|---:|---:|---:|
| Production-equation timeline clone | +11,827.550 | +6,731.937 | 13,000.347 | 18,836.428 | 72,691.089 | 242 / 1 / 1,495 |
| Packet nominal + SharedLog | -1,154.066 | -426.639 | 4,271.171 | 6,691.660 | 57,470.350 | 976 / 0 / 762 |
| Observed state + PercentageFirst | +14,775.045 | +8,562.066 | 14,892.471 | 20,548.702 | 72,609.566 | 33 / 0 / 1,705 |
| Observed state + RateFirst | -11,276.940 | -7,208.017 | 11,464.293 | 15,630.653 | 84,159.725 | 1,683 / 0 / 55 |
| Observed state + Shared Shapley | +1,749.053 | +610.716 | 2,853.887 | 5,070.992 | 59,851.340 | 536 / 0 / 1,202 |
| Observed state + SharedLog | +1,713.191 | +625.980 | 2,838.132 | 5,053.802 | 59,382.670 | 533 / 0 / 1,205 |

`SharedLog` removes `80.94%` of the rate-overlap MAE relative to observed-state PercentageFirst, but it is not parity and still has a strong positive sign imbalance.

## Overlap localization

The overlap groups are non-exclusive because one fight-level reference can contain several event states. They are used to show residual structure, not as independent samples.

| Group | N | PercentageFirst mean / MAE | RateFirst mean / MAE | SharedLog mean / MAE |
|---|---:|---:|---:|---:|
| Percentage + Crit-only | 1,573 | +13,160.301 / 13,243.423 | -10,169.636 / 10,353.188 | +1,460.457 / 2,589.960 |
| Percentage + DH-only | 26 | +13,778.695 / 13,778.695 | -5,566.791 / 5,594.285 | +4,062.817 / 4,439.397 |
| Percentage + Crit+DH | 533 | +32,192.359 / 32,369.982 | -21,534.044 / 21,771.048 | +5,199.898 / 6,024.844 |
| Multiple Crit providers | 639 | +27,050.064 / 27,067.721 | -19,174.419 / 19,317.070 | +3,801.453 / 4,780.399 |
| Multiple DH providers | 4 | +46,826.447 / 46,826.447 | -27,752.580 / 27,752.580 | +8,587.710 / 8,587.710 |
| Crit+DH from different providers | 19 | +11,474.113 / 11,474.113 | -5,880.047 / 5,887.017 | +2,766.240 / 3,281.560 |
| Percentage + guaranteed Crit | 373 | +32,562.300 / 32,754.783 | -23,159.862 / 23,249.905 | +4,609.945 / 5,657.575 |
| Percentage + guaranteed CDH | 395 | +8,549.859 / 8,549.859 | -7,238.604 / 7,310.337 | +668.510 / 2,064.526 |

Even after excluding periodic and every known guaranteed action, 262 rate-overlap direct-normal constraints retain:

| Candidate | Mean | MAE | Max abs |
|---|---:|---:|---:|
| PercentageFirst | +17,678.504 | 17,678.504 | 71,264.823 |
| RateFirst | -13,146.007 | 13,866.279 | 59,405.379 |
| SharedLog | +2,150.556 | 2,176.010 | 40,015.319 |

Therefore the primary structure is not a guaranteed-hit-only or DoT-only artifact. The remaining SharedLog residual is largest for Crit+DH and multiple-provider exposure, exactly where unavailable actual `Cu/Du` and rate-component inputs matter most.

## Provider cross-check

Under ordinary observed-state PercentageFirst, every well-covered provider shows the no-rate/rate split:

| Buff | No-rate N / MAE | Rate N / MAE |
|---|---:|---:|
| Standard Finish partner | 1 / ~0 | 107 / 36,193.447 |
| The Balance | 0 / unavailable | 45 / 26,695.677 |
| Divination | 39 / 1,562.221 | 220 / 18,697.129 |
| Starry Muse | 61 / 527.463 | 205 / 14,604.920 |
| Technical Finish | 165 / 1,525.018 | 535 / 13,006.306 |
| Searing Light | 54 / 1,145.360 | 163 / 12,807.905 |
| Embolden | 35 / 1,212.652 | 119 / 12,448.916 |
| Brotherhood | 31 / 1,053.681 | 88 / 10,609.456 |
| Mage's Ballad | 42 / 14.808 | 7 / 258.583 |

This is a shared component boundary, not one provider multiplier or target-scope bug.

## Matched controls

The cache contains 2,918 same-actor, same-report, same-fight, same-encounter, same-action-family groups with both percentage-only and rate-overlap events. They are quality-A state matches.

They show that the forward percentage basis changes only when rate state changes. They cannot supply per-event FFLogs residuals because the public API reference is only provider/recipient/fight aggregate. The CSV labels that limitation on every row rather than inventing a per-event target.

## Window, expiry, and packet ordering

Across registry-known percentage statuses, the audit records 28,324 intervals:

- 15,386 end through explicit remove;
- 11,338 end through refresh/overwrite;
- 13,468 intervals have damage between nominal and observed endpoints;
- same-timestamp damage ordering includes 5,843 packets before apply, 425 after apply, 4,863 before remove, and 156 after remove.

Timestamp sorting alone cannot classify those packets. `AttributionSequence` is required.

Technical Finish has 5,211 recipient intervals:

- observed-state only: 1,750 events / 62,689,361 damage;
- nominal-state only: 22 events / 406,842 damage;
- same-timestamp: 148 before apply, 20 after apply, 127 before remove, 4 after remove.

The existing strict controls remain decisive:

- single fixed percentage, direct/no-rate/no-guaranteed/no-pet: nominal `2/8` exact; packet+explicit `8/8` exact;
- clean multi-buff direct: packet+explicit `22/24` exact, MAE `656.236`;
- no-rate direct: `109/111` exact, MAE `141.889`;
- no-rate periodic: MAE `1,500.888`; hit-time replacement is worse, so application snapshot remains supported.

For an offline FFLogs parity replay, explicit apply/refresh/remove and packet sequence are necessary. A live production estimator cannot know whether a future remove packet will arrive. Replacing nominal expiry unconditionally would create a missing-remove failure mode and is not justified by this cache-only result. A production patch needs a state policy with explicit remove/refresh/death handling plus a documented bounded fallback, then dedicated missing-remove tests.

## Answers to the required questions

1. **Why does no-rate direct nearly match while rate-overlap fails?** The fixed multiplier/removal/log split is correct in isolation. The failure begins when percentage and rate components compete for the multiplicative interaction, compounded by burst-window endpoint differences.
2. **Which layers are wrong?** Packet/state eligibility is confirmed. The current percentage-first component basis is inconsistent with the FFLogs aggregate breakdown. Periodic state is a smaller residual. Same-timestamp ordering is already correct in the harness. The exact FFLogs cross-component allocation remains unidentified.
3. **Does percentage use the wrong base under rate buffs?** Relative to FFLogs `taken[]`, **yes as a component breakdown**: full `N` over-credits percentage. The exact replacement base is not yet proven.
4. **Does rate use a percentage-rewritten wrong base?** Production deliberately uses `N/M`, consistent with its percentage-first pipeline and public regular math. There is no evidence of an accidental second rewrite.
5. **Double attribution/removal?** **No.** All order variants conserve the same combined removal; the error is ownership of the interaction, not duplicated damage.
6. **Must nominal expiry be replaced by real state?** For offline parity, **yes**. For live production, explicit state must be combined with a defensible missing-event fallback; this round does not prove an unconditional policy.
7. **Post-fix rate-overlap percentage MAE?** No production fix was accepted, so production remains `14,892.471` under the observed-state percentage-first control. The best non-production diagnostic is `2,838.132` and still fails parity.
8. **Original 100 final rDPS MAE?** Production is unchanged; it remains `364.088` rDPS.
9. **Ready to resume guaranteed equation identification?** **NO.** Percentage/rate component ownership is still not at parity, and its residual is structured by Crit+DH/multiple-provider exposure.

## Verification

- Harness Release build: 0 warnings / 0 errors; self-test and final 106-fight ordering replay passed.
- Original 100 replay: raw damage 100/100 exact (`1,765,060,623`), direct packets `289,318/289,318`, unmatched direct 0, normalization warnings 0; final rDPS mean/median/MAE/max remain `-359.325/-350.242/364.088/1,151.696`.
- Cache-only attribution matrix, 11-fight Devilment probe, and guaranteed diagnostic replay passed; no guaranteed candidate was promoted.
- Package, Host, and Legacy Resource smoke passed. The full Release solution build completed with 0 errors; its 555 warnings are pre-existing vendor/IINACT warnings, while the harness project itself is clean.
- `git diff --check` and targeted formatter verification passed. Production `RaidDpsEstimator.cs` has no diff.
- No new FFLogs data was requested. No commit, push, PR, install, or publish was performed.

## Minimum next evidence

Do not collect random fights. The minimum discriminator is event-local, not a larger aggregate corpus:

1. 5-10 cached or targeted same-actor windows where FFLogs scripting/calculated events expose active percentage and Crit/DH status-effect totals;
2. one Crit-only and one DH-only normal-hit control with known actual `Cu/Du`;
3. one Crit+DH overlap with two distinct providers;
4. the existing Technical endpoint proof cases replayed through a proposed live-safe explicit-state fallback.

These distinguish `SharedBaseLog` from a different FFLogs table-component rule and quantify how much of the remaining 2,838 damage MAE is actual rate-input error. Until then, changing production would replace one known bias with an unproven formula.
