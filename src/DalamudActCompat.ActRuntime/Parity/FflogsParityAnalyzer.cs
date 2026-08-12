namespace DalamudActCompat.ActRuntime.Parity;

/// <summary>
/// Orchestrates independent Phase 0 diagnostic components. It intentionally
/// does not calculate or publish user-facing DPS/rDPS values.
/// </summary>
internal static class FflogsParityAnalyzer
{
    internal static FflogsParityDiagnostic Analyze(
        Guid encounterId,
        string zone,
        string encounterName,
        DateTimeOffset? fightStart,
        DateTimeOffset? fightEnd,
        IReadOnlyList<ParityReplayEvent> normalizedEvents,
        IReadOnlyList<ParityReplayEvent> rawEvents,
        IReadOnlySet<string> partyActorIds,
        ParityCaptureHealth captureHealth,
        ParityReferenceSnapshot? reference = null)
    {
        ArgumentNullException.ThrowIfNull(normalizedEvents);
        ArgumentNullException.ThrowIfNull(rawEvents);
        ArgumentNullException.ThrowIfNull(partyActorIds);

        var actors = ParityActorRegistry.Create(normalizedEvents.Concat(rawEvents), partyActorIds);
        var ledger = ParityDamageLedger.Build(normalizedEvents, actors);
        var included = ledger.Where(static entry => entry.Decision == ParityLedgerDecision.Included).ToArray();
        var rawDamage = ParityDamageLedger.ResolveRawNetworkDamage(rawEvents, actors);
        var targetability = normalizedEvents
            .Concat(rawEvents)
            .Where(static item => item.Kind == ParityReplayEventKind.Targetability)
            .GroupBy(static item => item.Sequence)
            .Select(static group => group.First())
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => item.Sequence)
            .ToArray();
        var durations = ParityEncounterTimeline.BuildDurations(
            fightStart,
            fightEnd,
            included,
            targetability,
            actors);
        var downtime = ParityEncounterTimeline.BuildDowntimeDiagnostics(
            included,
            targetability,
            durations,
            actors);
        var actorDiagnostics = ParityDamageLedger.BuildActorDiagnostics(ledger, actors);
        var includedDamage = included.Sum(static entry => entry.Amount);
        var excludedDamage = ledger
            .Where(static entry => entry.Decision == ParityLedgerDecision.Excluded)
            .Sum(static entry => Math.Max(0, entry.Amount));
        var breakdown = ParityDeltaAnalyzer.Build(
            ledger,
            rawEvents,
            rawDamage,
            includedDamage,
            reference,
            actors);

        var unknowns = new List<string>
        {
            "FFLogs actor active-time state machine is not publicly specified; ActorActiveTime uses first-to-last included damage observation and is labelled accordingly.",
            "FFLogs phase labels and targetable filtering are not inferred from NameToggle alone; phase names in downtime rows are observational labels.",
            "Raw DoT crit/direct flags and ownership may be synthesized or redirected by FFXIV_ACT_Plugin, so the normalized MasterSwing layer remains authoritative for current DACT accounting.",
        };
        if (reference is null || reference.Precision != ParityReferencePrecision.Exact)
        {
            unknowns.Add(
                "An exact FFLogs event export was not supplied; rounded table values can bound the total delta but cannot assign it to exact FFLogs event categories.");
        }
        if (captureHealth.PartialCapture ||
            captureHealth.LedgerTruncated ||
            captureHealth.DroppedRawLogLines > 0 ||
            captureHealth.DroppedNormalizedActions > 0)
        {
            unknowns.Add(
                "Capture health is incomplete; totals from this diagnostic must not be treated as parity evidence until replayed from a complete fixture.");
        }

        return new FflogsParityDiagnostic(
            "0.1",
            encounterId,
            zone,
            encounterName,
            durations,
            includedDamage,
            excludedDamage,
            rawDamage,
            includedDamage - rawDamage,
            actorDiagnostics,
            actorDiagnostics.Select(static actor => new ParityAttributionDiagnostic(
                actor.ActorId,
                [],
                [],
                [],
                null,
                null,
                null,
                null,
                null,
                "Deferred in Phase 0; no RaidDpsEstimator values are copied into the parity model."))
                .ToArray(),
            ledger,
            downtime,
            breakdown,
            captureHealth,
            reference,
            unknowns);
    }
}
