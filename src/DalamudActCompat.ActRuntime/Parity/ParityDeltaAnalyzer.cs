namespace DalamudActCompat.ActRuntime.Parity;

internal static class ParityDeltaAnalyzer
{
    public static IReadOnlyList<ParityDeltaBreakdown> Build(
        IReadOnlyList<ParityDamageLedgerEntry> ledger,
        IReadOnlyList<ParityReplayEvent> rawEvents,
        long rawDamage,
        long includedDamage,
        ParityReferenceSnapshot? reference,
        ParityActorRegistry actors)
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
                $"{group.Count()} normalized ACT events were excluded")));
        result.Add(new ParityDeltaBreakdown(
            "ParserNormalizationAdjustment",
            includedDamage - rawDamage,
            null,
            null,
            null,
            null,
            ParityReferencePrecision.Exact,
            "Normalized party damage minus provisionally party-owned raw 21/22/24 damage; includes parser source redirects and DoT simulation."));

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
                "FFLogs amount was displayed to 0.01 million; the exact delta is bounded by the display rounding interval."));
        }
    }
}
