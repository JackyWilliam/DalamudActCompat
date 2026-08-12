namespace DalamudActCompat.ActRuntime.Parity;

internal static class ParityDeltaAnalyzer
{
    public static IReadOnlyList<ParityDeltaBreakdown> Build(
        IReadOnlyList<ParityDamageLedgerEntry> ledger,
        IReadOnlyList<ParityReplayEvent> rawEvents,
        long rawDamage,
        long includedDamage,
        ParityReferenceSnapshot? reference,
        ParityActorRegistry actors,
        ParityReconciliationDiagnostic reconciliation)
    {
        var result = ledger
            .Where(static item => item.Decision == ParityLedgerDecision.Included)
            .GroupBy(static item => item.DamageKind)
            .Select(group => new ParityDeltaBreakdown(
                $"Included/{group.Key}",
                group.Sum(static item => item.Amount),
                null,
                null,
                null,
                null,
                ParityReferencePrecision.Unknown,
                ParityEvidenceStatus.Observed,
                $"{group.Count()} normalized ACT events entered DamageLedger"))
            .ToList();
        result.AddRange(ledger
            .Where(static item => item.Decision == ParityLedgerDecision.Excluded)
            .GroupBy(static item => item.ExclusionReason)
            .Select(group => new ParityDeltaBreakdown(
                $"Excluded/{group.Key}",
                group.Sum(static item => Math.Max(0, item.Amount)),
                null,
                null,
                null,
                null,
                ParityReferencePrecision.Exact,
                ParityEvidenceStatus.Observed,
                $"{group.Count()} normalized ACT events were excluded")));
        result.Add(new ParityDeltaBreakdown(
            "ParserNormalizationAdjustment",
            includedDamage - rawDamage,
            null,
            null,
            null,
            null,
            ParityReferencePrecision.Exact,
            ParityEvidenceStatus.Observed,
            "Normalized party damage minus provisionally party-owned raw 21/22/24 damage; includes parser source redirects and DoT simulation."));

        var knownParserDeltas = reconciliation.Events
            .Where(static item => item.Status != ParityCorrelationStatus.Ambiguous)
            .Select(static item => new
            {
                item.Category,
                item.Status,
                Delta = item.Status switch
                {
                    ParityCorrelationStatus.Matched => item.IncludedAmount -
                                                       (item.RawPartyOwned ? item.RawAmount ?? 0 : 0),
                    ParityCorrelationStatus.UnmatchedNormalized => item.IncludedAmount,
                    ParityCorrelationStatus.UnmatchedRaw => -(item.RawAmount ?? 0),
                    _ => 0,
                },
            })
            .Where(static item => item.Delta != 0)
            .GroupBy(static item => item.Category, StringComparer.Ordinal)
            .Select(group => new ParityDeltaBreakdown(
                $"ParserNormalization/{group.Key}",
                group.Sum(static item => item.Delta),
                null,
                null,
                null,
                null,
                ParityReferencePrecision.Exact,
                group.All(static item => item.Status == ParityCorrelationStatus.Matched)
                    ? ParityEvidenceStatus.Observed
                    : ParityEvidenceStatus.Unknown,
                $"{group.Count()} correlated rows; expand Reconciliation.Events for packet-level evidence"))
            .ToArray();
        result.AddRange(knownParserDeltas);
        var knownParserDelta = knownParserDeltas.Sum(static item => item.LocalAmount);
        if (knownParserDelta != includedDamage - rawDamage)
        {
            result.Add(new ParityDeltaBreakdown(
                "ParserNormalization/AmbiguousOrUncapturedResidual",
                includedDamage - rawDamage - knownParserDelta,
                null,
                null,
                null,
                null,
                ParityReferencePrecision.Unknown,
                ParityEvidenceStatus.Unknown,
                "The exact layer-total adjustment is conserved, but ambiguous or absent correlations prevent event-level allocation."));
        }

        var friendlyFire = rawEvents
            .Where(static item => item.Kind == ParityReplayEventKind.Damage && item.IsDamageSwing)
            .Where(item =>
                actors.IsPartyActor(item.SourceId, item.SourceName) &&
                actors.IsPartyActor(item.TargetId, item.TargetName))
            .Sum(static item => Math.Max(0, item.Amount));
        result.Add(new ParityDeltaBreakdown(
            "RawCandidate/FriendlyFire",
            friendlyFire,
            null,
            null,
            null,
            null,
            ParityReferencePrecision.Inferred,
            ParityEvidenceStatus.Inferred,
            "Party-owned raw damage whose target also resolves to a party actor; FFLogs comparison requires exact event export."));

        var estimatedOverkill = rawEvents
            .Where(static item =>
                item.Kind == ParityReplayEventKind.Damage &&
                item.IsDamageSwing &&
                item.Amount > 0 &&
                item.TargetCurrentHp is >= 0)
            .Where(item =>
                item.IsPartyMember ||
                actors.IsPartyActor(item.SourceId, item.SourceName) ||
                actors.IsPartyActor(item.OwnerId, string.Empty))
            .GroupBy(static item => new
            {
                item.Timestamp,
                item.PacketId,
                Target = ParityActorRegistry.ActorKey(item.TargetId, item.TargetName),
            })
            .Sum(static group =>
            {
                var targetHp = group.Select(static item => item.TargetCurrentHp!.Value).Max();
                return Math.Max(0, group.Sum(static item => item.Amount) - targetHp);
            });
        result.Add(new ParityDeltaBreakdown(
            "RawCandidate/EstimatedOverkill",
            estimatedOverkill,
            null,
            null,
            null,
            null,
            ParityReferencePrecision.Inferred,
            ParityEvidenceStatus.Inferred,
            "TargetCurrentHp is packet-level evidence, not yet paired to every normalized event; this is diagnostic only."));

        AddReferenceDelta(result, reference, includedDamage);
        return result;
    }

    private static void AddReferenceDelta(
        ICollection<ParityDeltaBreakdown> result,
        ParityReferenceSnapshot? reference,
        long includedDamage)
    {
        if (reference?.ExactTotalDamage is long exactReference)
        {
            result.Add(new ParityDeltaBreakdown(
                "Reference/TotalDamageDelta",
                includedDamage,
                exactReference,
                includedDamage - exactReference,
                includedDamage - exactReference,
                includedDamage - exactReference,
                ParityReferencePrecision.Exact,
                ParityEvidenceStatus.Observed,
                reference.Source));
        }
        else if (reference?.RoundedTotalDamageMillions is double roundedReference)
        {
            var center = checked((long)Math.Round(roundedReference * 1_000_000));
            const long halfDisplayUnit = 5_000;
            result.Add(new ParityDeltaBreakdown(
                "Reference/TotalDamageDelta",
                includedDamage,
                center,
                includedDamage - center,
                includedDamage - (center + halfDisplayUnit),
                includedDamage - (center - halfDisplayUnit),
                ParityReferencePrecision.Rounded,
                ParityEvidenceStatus.Observed,
                "FFLogs amount was displayed to 0.01 million; the exact delta is bounded by the display rounding interval."));
        }
    }
}
