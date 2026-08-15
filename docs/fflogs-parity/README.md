# FFLogs Parity Diagnostics

Exact rDPS parity research is paused, not deleted. Production presents a local Estimated rDPS model (`SharedBaseLog-v1`) while FFLogs Parse estimates use actual DACT DPS. The retained parity tooling remains a developer-only sidecar and does not block product releases.

Ordinary Debug mode does not start parity capture. A developer must explicitly enable `Record FFLogs parity diagnostics` or launch a cache-only harness mode. When explicitly enabled, a completed encounter writes JSON and Markdown reports under the plugin config directory at `logs/parity/`. The recorder observes raw pipe lines and normalized ACT `MasterSwing` callbacks, but never writes values back to `EncounterData`, `RaidDpsEstimator`, encounter snapshots, or the UI.

## Model boundaries

```text
raw 03/21/22/24/25/34 lines
  -> FflogsParityRawNormalizer
  -> raw canonical events --------------------┐
                                               ├-> ParityActorRegistry
ACT MasterSwing callbacks                      ├-> ParityDamageLedger
  -> normalized canonical events -------------├-> ParityEncounterTimeline
                                               ├-> ParityDeltaAnalyzer
                                               └-> JSON + Markdown report
```

- `ParityActorRegistry` keeps stable actor/owner evidence separate from name-based ledger decisions.
- `ParityDamageLedger` records every normalized action's include/exclude decision and reason.
- `ParityEncounterTimeline` reports fight, damage-wall, current union-downtime, all-targets-unavailable, and actor observed-span clocks separately.
- `ParityDeltaAnalyzer` reports local categories and reference precision; it does not convert an unexplained residual into a correction coefficient.
- `ParityEventReconciler` pairs raw, normalized, ledger, and optional reference events. It accepts only unique packet identities or complete timestamp/source/target/ability/amount identities; weak matches remain `ambiguous` or `unmatched`.
- `ParityReferenceFightImporter` loads developer-supplied JSON or CSV reference events. It is not used by normal DACT runtime and never calls the FFLogs API.
- Attribution fields are present in the schema but explicitly deferred. Phase 0 does not copy the current estimator into a new model.

## Dual-layer fixtures

Fixtures live in `tests/DalamudActCompat.PackageSmokeTests/Fixtures/FflogsParity/`.

1. `*.raw.json` stores recorded FFXIV_ACT_Plugin pipe lines plus party actor IDs. Replay exercises raw decoding, actor/pet ownership, and targetability normalization.
2. `*.normalized.json` stores canonical events after parser normalization. Replay exercises deterministic ledger decisions, crit/direct aggregation, duration clocks, and downtime math without requiring the compiled parser.

Both layers declare expected included damage and current diagnostic duration. Package smoke tests replay both and verify that excluded events retain their reasons.

Phase 1A adds `event-reconciliation.json`, which contains both layers plus an optional exact reference-file path. It covers packet correlation, aggregate DoT ambiguity, friendly targets, environment damage, overkill, boss/trash targets, pet respawn, duplicate normalization, large packets, and unmatched events. The report includes raw/normalized/owner conservation equations and Top 50 event/actor/ability/target views.

## Reference import schema

The JSON shape is `ParityReferenceFight` (`fight`, `actors`, `targets`, and `damageEvents`). The CSV header is:

```text
timestamp,sequence,sourceId,sourceInstanceId,sourceName,targetId,targetInstanceId,targetName,abilityId,abilityName,amount,overkill,absorbed,critical,directHit,packetId
```

CSV timestamps are ISO-8601. `packetId`, instance IDs, overkill, and absorbed are optional. `amount` is the reference event's effective damage value; importing it only enables diagnostics and never overwrites local totals.

Run the regression suite with:

```powershell
$dalamud = 'C:\Users\jacky\AppData\Roaming\XIVLauncherCN\addon\Hooks\dev\'
dotnet build .\DalamudActCompat.slnx -c Release "-p:DalamudLibPath=$dalamud"
& .\tests\DalamudActCompat.PackageSmokeTests\bin\Release\net10.0-windows10.0.17763.0\DalamudActCompat.PackageSmokeTests.exe
```

Regenerate the packaged Combat Quality snapshot from the fixed 100-fight cache with:

```powershell
dotnet run --project .\tools\DalamudActCompat.FflogsParityHarness -- `
  --production-candidate-evaluation `
  --quality-snapshot .\src\DalamudActCompat\Assets\Quality\combat-quality.json
```

This mode is replay-only by construction and never expands the public FFLogs corpus. The exact-parity research can be reopened when a strict DH-only longitudinal control, stronger actor stats, official API evidence, or an authoritative implementation becomes available.

The historical 2026-08-12 case report is in `phase0-2026-08-12-red-hot-and-deep-blue.md`. Because that encounter predates the normalized-event recorder and the FFLogs evidence is rounded, its remaining event-level delta is marked unknown rather than fitted.
