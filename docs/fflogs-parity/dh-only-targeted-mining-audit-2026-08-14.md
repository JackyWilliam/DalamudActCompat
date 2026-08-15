# FFLogs DH-only targeted ownership mining audit — 2026-08-14

## Decision

Percentage × rate ownership remains **Not determined**.

The bounded public-data scope is exhausted without a valid `DH-only ↔ Crit+DH`
normal-direct matched control. No ownership candidate can be compared on the requested
difference-in-differences axis, so the existing VPR actor flip remains **Still present**.

Production code, the ownership equation, guaranteed attribution, the two-second causal
status fallback, and the reference data were not changed.

## A. Mining

The `--matched-ownership-dh-audit` mode is cache-only. It reconstructs the existing
ranking-to-report actor rows, uses the authoritative offensive-buff registry, and cannot
construct an FFLogs API client or issue a request.

| Metric | Count |
|---|---:|
| Existing matched-cache API responses inventoried | 396 |
| Existing ranking pages inspected | 72 |
| Historical metadata preflight operations | 245 |
| Unique metadata responses inspected | 244 |
| Reconstructed target actor/fight rows | 278 |
| Actor/fight rows containing a DH-rate aggregate | 72 |
| New requests | 0 |
| Full events downloaded | 0 |

The 396-response matched-cache inventory contains 60 ranking, 244 metadata, 8 identity,
28 report/fight-index, and 56 other cached sample/event responses. The audit additionally
reuses 12 legacy DNC ranking responses, producing the 72 ranking-page total. It parses all
ranking and metadata payloads needed for preflight reconstruction; identity, report-index,
and event payloads for rows that cannot pass the metadata gate are inventoried but do not
override that gate.

FFLogs can retain multiple `masterData` actor IDs for the same character across report
segments. The audit resolves only the ID present in the selected fight's damage table,
while preserving the name, job, world, region, and partition checks. This avoids choosing
an arbitrary duplicate actor ID.

## B. DH-only gate

Strict fight-level DH-only requires all of the following in the recipient's actual FFLogs
`taken[]` metadata:

- at least one authoritative DH-rate status;
- no Crit-rate status;
- at least one known-magnitude percentage status;
- no unknown-magnitude percentage status.

Because BRD song phases are mutually exclusive, a second preflight looks for a narrower
component-window possibility: a known fixed percentage component plus BRD DH exposure,
where the only fight-level Crit source is Wanderer's Minuet and there is no external Crit
provider. This is only a metadata hypothesis; it is not counted as actual DH-only exposure.

| Gate | Count |
|---|---:|
| Strict fight-level DH-only candidates | 0 |
| Known-percentage BRD DH-window near candidates | 10 |
| Near candidates with unknown-Coda Radiant Finale | 10 |
| Fights allowed through to event download | 0 |
| A-grade pairs | 0 |
| B-grade pairs | 0 |

The ten rejected near candidates are:

| Actor | Job | World | Fight | Encounter | Known fixed percentage components |
|---|---|---|---|---:|---|
| Akemi | DNC | 太阳海岸 | `nq1YbBWFpN96HxfQ:1` | 101 | Mage's Ballad; Searing Light |
| 艾雅若拉 | PCT | 红玉海 | `TZ7rwx2QLdpn1V3a:1` | 101 | Divination; Mage's Ballad; Dokumori; The Spear |
| 一条薰· | SAM | 幻影群岛 | `WYQa3thjgCG8T6nV:1` | 101 | Mage's Ballad; Arcane Circle; Starry Muse |
| 一条薰· | SAM | 幻影群岛 | `Vf26ajkv9AnC8LHg:4` | 103 | Divination; Mage's Ballad; Arcane Circle; The Balance |
| 三字母 | SAM | 琥珀原 | `Mrt8ypWP14cwzG63:10` | 102 | Brotherhood; Mage's Ballad |
| 惯坏 | SAM | 红茶川 | `Dgfm9nc1yhK3tJQW:23` | 101 | Divination; Mage's Ballad; Searing Light; The Balance |
| 爱吃烧仙草 | SAM | 静语庄园 | `Dzdhc1FAJHq8pZ36:3` | 104 | Mage's Ballad; Starry Muse |
| 秋水苍 | SAM | 宇宙和音 | `j4QcxtFRznBK789H:2` | 101 | Brotherhood; Mage's Ballad |
| 恻隐心 | VPR | 拉诺西亚 | `2fapkJD3dK79PZQT:3` | 102 | Mage's Ballad; Starry Muse |
| 绚烂旋律 | WAR | 潮风亭 | `nqfCvcMmgkt6F9JR:2` | 101 | Embolden; Divination; Mage's Ballad |

All ten contain Radiant Finale with an unknown 2%/4%/6% Coda magnitude in the same
recipient aggregate. They therefore fail the user's explicit unknown-magnitude gate before
identity or event download. Rate providers and magnitudes remain in the generated preflight
CSV; actual overlap intervals and eligible normal-direct damage are unavailable because no
row was authorized for event download.

## C. Rejected longitudinal near pairs

Six same cached-identity-key comparisons share a fixed percentage component. None is a
canonical matched actor observation: every comparison has different encounters and contains
unknown-Coda Radiant Finale on at least its DH-window side. `canonicalID` was deliberately
not requested after the metadata gate failed.

| Actor | Job | World | DH-window candidate | Crit+DH comparison | Encounter change | Shared component | Grade |
|---|---|---|---|---|---|---|---|
| 艾雅若拉 | PCT | 红玉海 | `TZ7rwx2QLdpn1V3a:1` | `HMTFGvQa3hRNCjKr:9` | 101 → 102 | Mage's Ballad | C |
| 三字母 | SAM | 琥珀原 | `Mrt8ypWP14cwzG63:10` | `WKxhY4VfAz17mRMr:1` | 102 → 101 | Mage's Ballad | C |
| 惯坏 | SAM | 红茶川 | `Dgfm9nc1yhK3tJQW:23` | `TH9GVFdQaymKYR6A:43` | 101 → 102 | Mage's Ballad | C |
| 秋水苍 | SAM | 宇宙和音 | `j4QcxtFRznBK789H:2` | `bgN1xYHth3r4WJLX:3` | 101 → 104 | Brotherhood | C |
| 恻隐心 | VPR | 拉诺西亚 | `2fapkJD3dK79PZQT:3` | `M8q3gYRhDmFjnzcH:13` | 102 → 104 | Mage's Ballad | C |
| 绚烂旋律 | WAR | 潮风亭 | `nqfCvcMmgkt6F9JR:2` | `px37maKBGL9WYjQN:1` | 101 → 102 | Mage's Ballad | C |

Combatant info, gear, item level, level, Crit/DH stats, Cu, and Du are unavailable for a
valid matched group because none passed the metadata gate. No value was inferred from parse
performance.

## D. Candidate result

| Candidate | Valid pairs | Observed shift | Candidate residual shift |
|---|---:|---|---|
| CurrentProduction | 0 | unavailable | unavailable |
| RateFirst | 0 | unavailable | unavailable |
| SharedBaseLog | 0 | unavailable | unavailable |
| SharedShapley | 0 | unavailable | unavailable |
| SharedShapley3 | 0 | unavailable | unavailable |

No absolute aggregate MAE is reused in place of the missing matched observation. The mined
evidence therefore cannot distinguish SharedBaseLog from SharedShapley3.

## E. Actor flip

**Still present.** No independent DH-only axis was added, so the existing
深眸似海蓝/BaseLog versus 弥生千鹤/Shapley3 normal-direct flip is unchanged. It cannot be
classified as dimension-specific or actor-hidden-input behavior from this evidence.

## F. Why this public scope cannot answer the question

The current ranking/cache/preflight scope has no fight whose recipient aggregate is DH-only.
Every BRD-based component-window possibility is contaminated by unknown-Coda Radiant Finale,
and none of its same-identity comparisons also holds encounter constant. Downloading events
would violate the metadata and WhyUseful gates: the fixed percentage reference would remain
unknown before candidate prediction, so no valid difference-in-differences observation could
be produced.

Ownership status: **Not determined**.

## G. Minimum next gap

The smallest discriminating replacement is exactly four fights:

- two canonical VPR actors;
- two partition-9 fights per actor, with the same encounter and same known fixed percentage
  component, preferably Divination 6% or Dokumori 5%;
- Fight A overlaps that component with Battle Voice or Army's Paeon, has no Crit-rate status,
  has no Wanderer's Minuet during the component, and has no Radiant Finale;
- Fight B preserves the same percentage and DH exposure and adds only Chain Stratagem or
  Battle Litany;
- both sides contain eligible normal-direct damage and exclude periodic, guaranteed-hit,
  death/reset, pet, and unknown-metadata cases.

This supplies two cross-actor `DH-only → Crit+DH` controls while changing only the Crit
dimension. Anything less leaves the existing actor flip non-identifiable.

## Reproducible artifacts

- `artifacts/fflogs-parity-harness/reports/dh-ownership-exhaustion-report.json`
- `artifacts/fflogs-parity-harness/reports/dh-ownership-preflights.csv`
- `artifacts/fflogs-parity-harness/reports/dh-ownership-near-pairs.csv`
- `artifacts/fflogs-parity-harness/reports/dh-ownership-targeted-mining-2026-08-14.md`

These artifacts are intentionally ignored cache/report outputs. This audit document is the
tracked decision record.
