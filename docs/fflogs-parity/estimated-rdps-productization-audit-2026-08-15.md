# Estimated rDPS productization audit — 2026-08-15

## Decision

- Production ownership model: `SharedBaseLog-v1`.
- Product meaning: Estimated rDPS. The combat-table label remains `rDPS` and its tooltip states that the value is estimated.
- This is a DACT estimator choice, not a confirmed or official FFLogs equation.
- Exact FFLogs rDPS parity research is paused. Its harness, caches, registries, and reports remain intact.
- The existing `Causal-2s` status lifecycle, guaranteed-hit equation, prior/clamp, Life Surge, and verified AST/BRD metadata are unchanged.

## Fixed 100-fight counterfactual

All values are DACT minus FFLogs rDPS. The evaluation is cache-only and replays the same normalized events through both ownership models.

| Model | N | Mean Δ | Median Δ | MAE | P90 \|Δ\| | P95 \|Δ\| | Max \|Δ\| |
|---|---:|---:|---:|---:|---:|---:|---:|
| PercentageFirst | 100 | -320.548 | -306.060 | 327.076 | 575.269 | 794.146 | 1089.019 |
| SharedBaseLog-v1 | 100 | -321.156 | -311.523 | 325.156 | 580.842 | 613.123 | 979.417 |

SharedBaseLog improves overall MAE, P95, and maximum error. P90 increases by 5.573. The largest per-fight absolute-error regression is 160.627 rDPS, while the global maximum falls by 109.603 rDPS. Encounter MAE changes range from -27.373 to +23.762; the largest recipient-job regression is +53.409 for RPR (`N=3`). No raw-damage, direct-packet, conservation, or clean percentage control changed.

The detailed generated outputs are:

- `artifacts/fflogs-parity-harness/reports/production-candidate-evaluation.json`
- `artifacts/fflogs-parity-harness/reports/production-candidate-evaluation.csv`
- `artifacts/fflogs-parity-harness/reports/production-candidate-evaluation.md`
- packaged quality snapshot: `src/DalamudActCompat/Assets/Quality/combat-quality.json`

## FFLogs Parse estimate

The display now has two independent branches:

```text
Combat events -> actual DACT DPS -> FFLogs DPS percentile -> Parse number/color
Combat events -> attribution engine -> Estimated rDPS -> main combat value
```

Percentile curves are keyed and validated by region, partition, metric, encounter, difficulty, and job. The current population is `CN / partition 9 / DPS`. Cache format v2 rejects older rDPS curves and incompatible partitions. Valid stale curves remain displayable with their update date while an authenticated background refresh is queued. Missing curves produce `--` with a neutral color.

## Developer quality surface

The Debug-only `Combat Quality` panel reads the generated snapshot and current percentile cache. It shows:

- Raw Damage and Direct Packets parity counts.
- rDPS model and status-engine identifiers.
- FFLogs region, partition, DPS metric, and latest percentile date.
- Samples, mean/median delta, MAE, P90/P95/max absolute delta.
- Harness last run and normalization warnings.

Ordinary Debug no longer records parity data. `Record FFLogs parity diagnostics` is a separate explicit opt-in; CLI harness modes remain available.

## Regression result

- Raw damage: `100/100` exact.
- Direct packet correlation: `289,318/289,318`.
- Normalization warnings: `0`.
- 100-sample production replay: passed with the SharedBaseLog metrics above.
- 106-fight attribution matrix: passed; 100 existing + 6 targeted fights, normalization warnings 0.
- Percentage controls: 106 fights / 2,217 constraints completed.
- Devilment probe: 11 fights / 1,553 actions completed.
- Guaranteed diagnostic: unchanged PercentageFirst ownership isolation, 1,451 events, maximum event calibration residual `9.82e-11`, equation status `Not determined`.
- Release solution build: 0 warnings / 0 errors.
- Package, Host, Legacy Resource, and harness self-tests: passed.
