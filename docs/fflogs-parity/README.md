# FFLogs Parity Diagnostics

Phase 0 is a developer-only sidecar. When DACT debug mode is enabled, a completed encounter writes JSON and Markdown reports under the plugin config directory at `logs/parity/`. The recorder observes raw pipe lines and normalized ACT `MasterSwing` callbacks, but never writes values back to `EncounterData`, `RaidDpsEstimator`, encounter snapshots, or the UI.

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
- Attribution fields are present in the schema but explicitly deferred. Phase 0 does not copy the current estimator into a new model.

## Dual-layer fixtures

Fixtures live in `tests/DalamudActCompat.PackageSmokeTests/Fixtures/FflogsParity/`.

1. `*.raw.json` stores recorded FFXIV_ACT_Plugin pipe lines plus party actor IDs. Replay exercises raw decoding, actor/pet ownership, and targetability normalization.
2. `*.normalized.json` stores canonical events after parser normalization. Replay exercises deterministic ledger decisions, crit/direct aggregation, duration clocks, and downtime math without requiring the compiled parser.

Both layers declare expected included damage and current diagnostic duration. Package smoke tests replay both and verify that excluded events retain their reasons.

Run the regression suite with:

```powershell
$dalamud = 'C:\Users\jacky\AppData\Roaming\XIVLauncherCN\addon\Hooks\dev\'
dotnet build .\DalamudActCompat.slnx -c Release "-p:DalamudLibPath=$dalamud"
& .\tests\DalamudActCompat.PackageSmokeTests\bin\Release\net10.0-windows10.0.17763.0\DalamudActCompat.PackageSmokeTests.exe
```

The historical 2026-08-12 case report is in `phase0-2026-08-12-red-hot-and-deep-blue.md`. Because that encounter predates the normalized-event recorder and the FFLogs evidence is rounded, its remaining event-level delta is marked unknown rather than fitted.
