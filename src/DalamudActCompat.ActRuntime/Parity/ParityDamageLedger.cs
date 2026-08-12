namespace DalamudActCompat.ActRuntime.Parity;

internal static class ParityDamageLedger
{
    public static IReadOnlyList<ParityDamageLedgerEntry> Build(
        IReadOnlyList<ParityReplayEvent> events,
        ParityActorRegistry actors)
    {
        var result = new List<ParityDamageLedgerEntry>();
        foreach (var item in events
                     .Where(static item => item.Kind == ParityReplayEventKind.Damage)
                     .OrderBy(static item => item.Timestamp)
                     .ThenBy(static item => item.Sequence))
        {
            var source = actors.Resolve(item.SourceId, item.SourceName);
            var owner = actors.Resolve(item.OwnerId, string.Empty);
            var isPartyOwned = item.IsPartyMember ||
                               actors.IsPartyActor(source) ||
                               actors.IsPartyActor(owner);
            var (decision, reason) = ResolveDecision(item, isPartyOwned);
            long? estimatedOverkill = item.TargetCurrentHp is >= 0 && item.Amount > item.TargetCurrentHp
                ? item.Amount - item.TargetCurrentHp.Value
                : null;
            result.Add(new ParityDamageLedgerEntry(
                item.Sequence,
                item.Timestamp,
                item.SourceId,
                item.SourceName,
                item.OwnerId,
                owner?.Name ?? string.Empty,
                item.TargetId,
                item.TargetName,
                item.AbilityId,
                item.AbilityName,
                item.Amount,
                estimatedOverkill,
                item.DamageKind,
                item.Critical,
                item.DirectHit,
                decision,
                reason,
                item.RawLineType,
                item.PacketId,
                item.Evidence));
        }
        return result;
    }

    public static long ResolveRawNetworkDamage(
        IReadOnlyList<ParityReplayEvent> rawEvents,
        ParityActorRegistry actors)
        => rawEvents
            .Where(static item =>
                item.Kind == ParityReplayEventKind.Damage &&
                item.IsDamageSwing &&
                item.Amount > 0)
            .Where(item =>
                item.IsPartyMember ||
                actors.IsPartyActor(item.SourceId, item.SourceName) ||
                actors.IsPartyActor(item.OwnerId, string.Empty))
            .Sum(static item => item.Amount);

    public static IReadOnlyList<ParityActorDiagnostic> BuildActorDiagnostics(
        IReadOnlyList<ParityDamageLedgerEntry> ledger,
        ParityActorRegistry actors)
        => ledger
            .GroupBy(
                static item => ParityActorRegistry.ActorKey(
                    item.OwnerId.Length > 0 ? item.OwnerId : item.SourceId,
                    item.OwnerName.Length > 0 ? item.OwnerName : item.SourceName),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.ToArray();
                var included = rows
                    .Where(static item => item.Decision == ParityLedgerDecision.Included)
                    .ToArray();
                var representative = rows.Last();
                var actorId = representative.OwnerId.Length > 0
                    ? representative.OwnerId
                    : representative.SourceId;
                var actor = actors.Resolve(
                    actorId,
                    representative.OwnerName.Length > 0
                        ? representative.OwnerName
                        : representative.SourceName);
                var activeSeconds = included.Length < 2
                    ? 0
                    : (included[^1].Timestamp - included[0].Timestamp).TotalSeconds;
                return new ParityActorDiagnostic(
                    actorId,
                    actor?.Name ?? representative.SourceName,
                    actor?.Job ?? string.Empty,
                    representative.OwnerId,
                    actor?.IsPartyMember == true,
                    included.Sum(static item => item.Amount),
                    rows.Where(static item => item.Decision == ParityLedgerDecision.Excluded)
                        .Sum(static item => Math.Max(0, item.Amount)),
                    included.Length,
                    included.Count(static item => item.Critical),
                    included.Count(static item => item.DirectHit),
                    included.Count(static item => item.Critical && item.DirectHit),
                    ResolveRate(included.Count(static item => item.Critical), included.Length),
                    ResolveRate(included.Count(static item => item.DirectHit), included.Length),
                    ResolveRate(included.Count(static item => item.Critical && item.DirectHit), included.Length),
                    activeSeconds,
                    "observed included-damage span; not claimed as FFLogs active time");
            })
            .OrderByDescending(static actor => actor.IncludedDamage)
            .ThenBy(static actor => actor.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static double ResolveRate(int count, int total)
        => total > 0 ? (double)count / total : 0;

    private static (ParityLedgerDecision Decision, ParityExclusionReason Reason) ResolveDecision(
        ParityReplayEvent item,
        bool isPartyOwned)
    {
        if (!item.IsDamageSwing)
        {
            return (ParityLedgerDecision.Excluded, ParityExclusionReason.NotDamageSwing);
        }
        if (!item.HasEncounter)
        {
            return (ParityLedgerDecision.Excluded, ParityExclusionReason.MissingEncounter);
        }
        if (item.Amount <= 0)
        {
            return (ParityLedgerDecision.Excluded, ParityExclusionReason.NonPositiveDamage);
        }
        if (!isPartyOwned)
        {
            return (
                ParityLedgerDecision.Excluded,
                string.IsNullOrWhiteSpace(item.SourceId) && string.IsNullOrWhiteSpace(item.SourceName)
                    ? ParityExclusionReason.UnresolvedActor
                    : ParityExclusionReason.NonPartySource);
        }
        return (ParityLedgerDecision.Included, ParityExclusionReason.None);
    }
}
