namespace DalamudActCompat.ActRuntime.Parity;

/// <summary>
/// Correlates diagnostic layers without changing DamageLedger decisions. A
/// match is accepted only when a packet ID or a complete event identity is
/// unique; incomplete candidates remain visible instead of being guessed.
/// </summary>
internal static class ParityEventReconciler
{
    private const int TopEventLimit = 50;

    public static ParityReconciliationDiagnostic Build(
        IReadOnlyList<ParityReplayEvent> rawEvents,
        IReadOnlyList<ParityReplayEvent> normalizedEvents,
        IReadOnlyList<ParityDamageLedgerEntry> ledger,
        ParityActorRegistry actors,
        ParityReferenceFight? reference)
    {
        var rawDamage = rawEvents
            .Where(static item => item.Kind == ParityReplayEventKind.Damage && item.Amount > 0)
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => item.Sequence)
            .ToArray();
        var normalizedDamage = normalizedEvents
            .Where(static item => item.Kind == ParityReplayEventKind.Damage)
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => item.Sequence)
            .ToArray();
        var ledgerBySequence = ledger.ToDictionary(static item => item.Sequence);
        var duplicateNormalizedSequences = FindDuplicateNormalizedSequences(normalizedDamage);
        var remainingNormalized = Enumerable.Range(0, normalizedDamage.Length).ToHashSet();
        var pending = new List<PendingCorrelation>();

        foreach (var raw in rawDamage)
        {
            var candidates = FindNormalizedCandidates(raw, normalizedDamage, remainingNormalized, packetOnly: true);
            var matchReason = "unique packet ID plus event identity";
            if (candidates.Count == 0)
            {
                candidates = FindNormalizedCandidates(raw, normalizedDamage, remainingNormalized, packetOnly: false);
                matchReason = "unique timestamp/source/target/ability/amount identity";
            }

            if (candidates.Count == 1)
            {
                var normalizedIndex = candidates[0];
                remainingNormalized.Remove(normalizedIndex);
                pending.Add(new PendingCorrelation(
                    raw,
                    normalizedDamage[normalizedIndex],
                    ParityCorrelationStatus.Matched,
                    matchReason,
                    1));
                continue;
            }

            if (candidates.Count > 1 || HasAggregateDotCandidates(raw, normalizedDamage, remainingNormalized))
            {
                pending.Add(new PendingCorrelation(
                    raw,
                    null,
                    ParityCorrelationStatus.Ambiguous,
                    "multiple candidates or aggregate DoT semantics prevent a reliable one-to-one match",
                    Math.Max(candidates.Count, CountAggregateDotCandidates(raw, normalizedDamage, remainingNormalized))));
                continue;
            }

            var partyOwned = raw.IsPartyMember ||
                             actors.IsPartyActor(raw.SourceId, raw.SourceName) ||
                             actors.IsPartyActor(raw.OwnerId, string.Empty);
            pending.Add(new PendingCorrelation(
                raw,
                null,
                partyOwned
                    ? ParityCorrelationStatus.UnmatchedRaw
                    : ParityCorrelationStatus.IntentionallyIgnored,
                partyOwned
                    ? "no unique normalized event was observed"
                    : "raw source is not party-owned in the diagnostic actor registry",
                0));
        }

        foreach (var index in remainingNormalized.Order())
        {
            pending.Add(new PendingCorrelation(
                null,
                normalizedDamage[index],
                ParityCorrelationStatus.UnmatchedNormalized,
                duplicateNormalizedSequences.Contains(normalizedDamage[index].Sequence)
                    ? "normalized event duplicates another exact normalized identity; upstream duplication candidate"
                    : "no unique raw network event was observed",
                0));
        }

        var correlations = AttachReferenceEvents(pending, ledgerBySequence, actors, reference);
        var conservation = BuildConservation(correlations, ledger);
        var deltaBasis = reference is null ? "raw-to-normalized" : "normalized-to-reference";
        return new ParityReconciliationDiagnostic(
            correlations,
            conservation,
            correlations
                .Where(static item => item.ReferenceAmount is not null &&
                                      item.NormalizedAmount - item.ReferenceAmount > 0)
                .OrderByDescending(static item => item.NormalizedAmount - item.ReferenceAmount)
                .Take(TopEventLimit)
                .ToArray(),
            correlations
                .Where(static item => item.Status == ParityCorrelationStatus.UnmatchedNormalized)
                .OrderByDescending(static item => item.NormalizedAmount ?? 0)
                .Take(TopEventLimit)
                .ToArray(),
            correlations
                .Where(static item => item.Status is ParityCorrelationStatus.UnmatchedRaw or ParityCorrelationStatus.Ambiguous)
                .OrderByDescending(static item => item.RawAmount ?? 0)
                .Take(TopEventLimit)
                .ToArray(),
            BuildAggregates(correlations, "actor", deltaBasis),
            BuildAggregates(correlations, "ability", deltaBasis),
            BuildAggregates(correlations, "target", deltaBasis),
            reference is null
                ? "No exact reference event file was supplied; positive DACT-vs-reference event ranking is unavailable."
                : $"Exact developer reference file supplied from {reference.Source}; normal runtime remains offline-only.");
    }

    private static IReadOnlyList<ParityReconciliationEvent> AttachReferenceEvents(
        IReadOnlyList<PendingCorrelation> pending,
        IReadOnlyDictionary<long, ParityDamageLedgerEntry> ledgerBySequence,
        ParityActorRegistry actors,
        ParityReferenceFight? reference)
    {
        var remainingReference = reference is null
            ? new HashSet<int>()
            : Enumerable.Range(0, reference.DamageEvents.Count).ToHashSet();
        var result = new List<ParityReconciliationEvent>(pending.Count + remainingReference.Count);
        foreach (var item in pending)
        {
            ParityReferenceDamageEvent? referenceEvent = null;
            if (reference is not null && item.Normalized is not null)
            {
                var candidates = FindReferenceCandidates(
                    item.Normalized,
                    reference.DamageEvents,
                    remainingReference,
                    packetOnly: true);
                if (candidates.Count == 0)
                {
                    candidates = FindReferenceCandidates(
                        item.Normalized,
                        reference.DamageEvents,
                        remainingReference,
                        packetOnly: false);
                }
                if (candidates.Count == 1)
                {
                    var index = candidates[0];
                    referenceEvent = reference.DamageEvents[index];
                    remainingReference.Remove(index);
                }
            }
            result.Add(Flatten(item, ledgerBySequence, actors, referenceEvent));
        }

        if (reference is not null)
        {
            foreach (var index in remainingReference.Order())
            {
                var item = reference.DamageEvents[index];
                result.Add(new ParityReconciliationEvent(
                    $"reference:{item.Sequence}",
                    ParityCorrelationStatus.UnmatchedReference,
                    item.Timestamp,
                    null,
                    null,
                    item.Sequence,
                    item.SourceId,
                    item.SourceName,
                    string.Empty,
                    string.Empty,
                    item.TargetId,
                    item.TargetName,
                    item.AbilityId,
                    item.AbilityName,
                    null,
                    false,
                    null,
                    0,
                    item.Amount,
                    item.PacketId,
                    string.Empty,
                    string.Empty,
                    null,
                    null,
                    "ReferenceOnly",
                    "no unique normalized event was observed",
                    ParityEvidenceStatus.Unknown,
                    0));
            }
        }
        return result
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => item.RawSequence ?? item.NormalizedSequence ?? item.ReferenceSequence)
            .ToArray();
    }

    private static ParityReconciliationEvent Flatten(
        PendingCorrelation correlation,
        IReadOnlyDictionary<long, ParityDamageLedgerEntry> ledgerBySequence,
        ParityActorRegistry actors,
        ParityReferenceDamageEvent? reference)
    {
        var canonical = correlation.Normalized ?? correlation.Raw!;
        ledgerBySequence.TryGetValue(correlation.Normalized?.Sequence ?? long.MinValue, out var ledger);
        var ownerId = canonical.OwnerId;
        var owner = actors.Resolve(ownerId, string.Empty);
        var category = ResolveCategory(correlation, ledger, actors);
        var evidenceStatus = category.EndsWith("Candidate", StringComparison.Ordinal)
            ? ParityEvidenceStatus.Inferred
            : correlation.Status switch
            {
                ParityCorrelationStatus.Matched => ParityEvidenceStatus.Observed,
                ParityCorrelationStatus.IntentionallyIgnored => ParityEvidenceStatus.Observed,
                ParityCorrelationStatus.Ambiguous => ParityEvidenceStatus.Unknown,
                _ => ParityEvidenceStatus.Unknown,
            };
        var includedAmount = ledger?.Decision == ParityLedgerDecision.Included ? ledger.Amount : 0;
        var rawPartyOwned = correlation.Raw is not null &&
                            (correlation.Raw.IsPartyMember ||
                             actors.IsPartyActor(correlation.Raw.SourceId, correlation.Raw.SourceName) ||
                             actors.IsPartyActor(correlation.Raw.OwnerId, string.Empty));
        return new ParityReconciliationEvent(
            BuildCorrelationId(correlation),
            correlation.Status,
            canonical.Timestamp,
            correlation.Raw?.Sequence,
            correlation.Normalized?.Sequence,
            reference?.Sequence,
            canonical.SourceId,
            canonical.SourceName,
            ownerId,
            owner?.Name ?? string.Empty,
            canonical.TargetId,
            canonical.TargetName,
            canonical.AbilityId,
            canonical.AbilityName,
            correlation.Raw?.Amount,
            rawPartyOwned,
            correlation.Normalized?.Amount,
            includedAmount,
            reference?.Amount,
            FirstNonEmpty(correlation.Raw?.PacketId, correlation.Normalized?.PacketId, reference?.PacketId),
            correlation.Normalized?.ParserEventId ?? string.Empty,
            correlation.Raw?.RawEventId ?? string.Empty,
            correlation.Raw?.TargetIndex,
            correlation.Raw?.EffectIndex,
            category,
            correlation.Reason,
            evidenceStatus,
            correlation.CandidateCount);
    }

    private static List<int> FindNormalizedCandidates(
        ParityReplayEvent raw,
        IReadOnlyList<ParityReplayEvent> normalized,
        IReadOnlySet<int> remaining,
        bool packetOnly)
    {
        if (packetOnly && string.IsNullOrWhiteSpace(raw.PacketId))
        {
            return [];
        }
        if (!packetOnly && !HasCompleteIdentity(raw))
        {
            return [];
        }
        var candidates = remaining
            .Where(index =>
                (!packetOnly || string.Equals(
                    raw.PacketId,
                    normalized[index].PacketId,
                    StringComparison.OrdinalIgnoreCase)) &&
                (packetOnly || raw.Timestamp == normalized[index].Timestamp) &&
                (packetOnly || raw.Amount == normalized[index].Amount) &&
                EquivalentSource(raw, normalized[index]) &&
                EquivalentIdentity(raw.TargetId, raw.TargetName, normalized[index].TargetId, normalized[index].TargetName) &&
                EquivalentAbility(raw, normalized[index]))
            .ToList();
        if (packetOnly && candidates.Count > 1)
        {
            var amountMatches = candidates.Where(index => raw.Amount == normalized[index].Amount).ToList();
            return amountMatches.Count > 0 ? amountMatches : candidates;
        }
        return candidates;
    }

    private static List<int> FindReferenceCandidates(
        ParityReplayEvent normalized,
        IReadOnlyList<ParityReferenceDamageEvent> reference,
        IReadOnlySet<int> remaining,
        bool packetOnly)
    {
        if (packetOnly && string.IsNullOrWhiteSpace(normalized.PacketId))
        {
            return [];
        }
        if (!packetOnly && !HasCompleteIdentity(normalized))
        {
            return [];
        }
        var candidates = remaining
            .Where(index =>
            {
                var candidate = reference[index];
                return (!packetOnly || string.Equals(
                            normalized.PacketId,
                            candidate.PacketId,
                            StringComparison.OrdinalIgnoreCase)) &&
                       (packetOnly || normalized.Timestamp == candidate.Timestamp) &&
                       (packetOnly || normalized.Amount == candidate.Amount) &&
                       EquivalentIdentity(
                           normalized.SourceId,
                           normalized.SourceName,
                           candidate.SourceId,
                           candidate.SourceName) &&
                       EquivalentIdentity(
                           normalized.TargetId,
                           normalized.TargetName,
                           candidate.TargetId,
                           candidate.TargetName) &&
                       EquivalentIdentity(
                           normalized.AbilityId,
                           normalized.AbilityName,
                           candidate.AbilityId,
                           candidate.AbilityName);
            })
            .ToList();
        if (packetOnly && candidates.Count > 1)
        {
            var amountMatches = candidates.Where(index => normalized.Amount == reference[index].Amount).ToList();
            return amountMatches.Count > 0 ? amountMatches : candidates;
        }
        return candidates;
    }

    private static bool HasAggregateDotCandidates(
        ParityReplayEvent raw,
        IReadOnlyList<ParityReplayEvent> normalized,
        IReadOnlySet<int> remaining)
        => CountAggregateDotCandidates(raw, normalized, remaining) > 0;

    private static int CountAggregateDotCandidates(
        ParityReplayEvent raw,
        IReadOnlyList<ParityReplayEvent> normalized,
        IReadOnlySet<int> remaining)
    {
        if (raw.DamageKind != ParityDamageKind.DamageOverTime)
        {
            return 0;
        }
        return remaining.Count(index =>
            normalized[index].DamageKind == ParityDamageKind.DamageOverTime &&
            normalized[index].Timestamp == raw.Timestamp &&
            EquivalentIdentity(
                raw.TargetId,
                raw.TargetName,
                normalized[index].TargetId,
                normalized[index].TargetName));
    }

    private static HashSet<long> FindDuplicateNormalizedSequences(
        IReadOnlyList<ParityReplayEvent> normalized)
        => normalized
            .GroupBy(static item => new
            {
                item.Timestamp,
                Source = ParityActorRegistry.ActorKey(item.SourceId, item.SourceName),
                Owner = ParityActorRegistry.ActorKey(item.OwnerId, string.Empty),
                Target = ParityActorRegistry.ActorKey(item.TargetId, item.TargetName),
                Ability = ParityActorRegistry.ActorKey(item.AbilityId, item.AbilityName),
                item.Amount,
                item.DamageKind,
            })
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group.Select(static item => item.Sequence))
            .ToHashSet();

    private static ParityDamageConservation BuildConservation(
        IReadOnlyList<ParityReconciliationEvent> correlations,
        IReadOnlyList<ParityDamageLedgerEntry> ledger)
    {
        var rawRows = correlations.Where(static item => item.RawAmount is > 0).ToArray();
        var rawTotal = rawRows.Sum(static item => item.RawAmount!.Value);
        var matched = rawRows
            .Where(static item => item.Status == ParityCorrelationStatus.Matched)
            .Sum(static item => item.RawAmount!.Value);
        var ignored = rawRows
            .Where(static item => item.Status == ParityCorrelationStatus.IntentionallyIgnored)
            .Sum(static item => item.RawAmount!.Value);
        var ambiguous = rawRows
            .Where(static item => item.Status == ParityCorrelationStatus.Ambiguous)
            .Sum(static item => item.RawAmount!.Value);
        var unmatched = rawRows
            .Where(static item => item.Status == ParityCorrelationStatus.UnmatchedRaw)
            .Sum(static item => item.RawAmount!.Value);
        var normalized = ledger.Sum(static item => Math.Max(0, item.Amount));
        var included = ledger
            .Where(static item => item.Decision == ParityLedgerDecision.Included)
            .Sum(static item => Math.Max(0, item.Amount));
        var excluded = ledger
            .Where(static item => item.Decision == ParityLedgerDecision.Excluded)
            .Sum(static item => Math.Max(0, item.Amount));
        // Owner grouping must only change the attribution key, never create a
        // second copy of a source event.
        var ownerAttributed = ledger
            .Where(static item => item.Decision == ParityLedgerDecision.Included)
            .GroupBy(static item => ParityActorRegistry.ActorKey(
                item.OwnerId.Length > 0 ? item.OwnerId : item.SourceId,
                item.OwnerName.Length > 0 ? item.OwnerName : item.SourceName))
            .Sum(static group => group.Sum(static item => Math.Max(0, item.Amount)));
        return new ParityDamageConservation(
            rawTotal,
            matched,
            ignored,
            ambiguous,
            unmatched,
            ambiguous + unmatched,
            rawTotal == matched + ignored + ambiguous + unmatched,
            normalized,
            included,
            excluded,
            normalized == included + excluded,
            included,
            ownerAttributed,
            included == ownerAttributed);
    }

    private static IReadOnlyList<ParityDeltaAggregate> BuildAggregates(
        IReadOnlyList<ParityReconciliationEvent> events,
        string dimension,
        string deltaBasis)
        => events
            .Select(item => new
            {
                Event = item,
                Delta = ResolveDelta(item, deltaBasis),
                Key = dimension switch
                {
                    "actor" => FirstNonEmpty(item.OwnerId, item.SourceId, item.SourceName),
                    "ability" => FirstNonEmpty(item.AbilityId, item.AbilityName),
                    _ => FirstNonEmpty(item.TargetId, item.TargetName),
                },
                Name = dimension switch
                {
                    "actor" => FirstNonEmpty(item.OwnerName, item.SourceName),
                    "ability" => item.AbilityName,
                    _ => item.TargetName,
                },
            })
            .Where(static item => item.Delta != 0 && !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(static item => (item.Key, item.Name))
            .Select(group => new ParityDeltaAggregate(
                dimension,
                group.Key.Key,
                group.Key.Name,
                group.Sum(static item => item.Delta),
                group.Count(),
                group.All(static item => item.Event.EvidenceStatus == ParityEvidenceStatus.Observed)
                    ? ParityEvidenceStatus.Observed
                    : ParityEvidenceStatus.Unknown))
            .OrderByDescending(static item => Math.Abs(item.Delta))
            .Take(TopEventLimit)
            .ToArray();

    private static long ResolveDelta(ParityReconciliationEvent item, string basis)
    {
        if (basis == "normalized-to-reference")
        {
            return item.ReferenceAmount is not null
                ? (item.NormalizedAmount ?? 0) - item.ReferenceAmount.Value
                : 0;
        }
        return (item.NormalizedAmount ?? 0) - (item.RawAmount ?? 0);
    }

    private static string ResolveCategory(
        PendingCorrelation correlation,
        ParityDamageLedgerEntry? ledger,
        ParityActorRegistry actors)
    {
        var item = correlation.Normalized ?? correlation.Raw!;
        if (item.DamageKind == ParityDamageKind.DamageOverTime)
        {
            return "DoTNormalizationCandidate";
        }
        if (item.DamageKind == ParityDamageKind.LimitBreak)
        {
            return "LimitBreak";
        }
        if (item.DamageKind == ParityDamageKind.Environment ||
            string.IsNullOrWhiteSpace(item.SourceId) && string.IsNullOrWhiteSpace(item.SourceName))
        {
            return "EnvironmentCandidate";
        }
        if (actors.IsPartyActor(item.TargetId, item.TargetName))
        {
            return "FriendlyTargetCandidate";
        }
        if (correlation.Raw?.EstimatedOverkill() is > 0 || ledger?.EstimatedOverkill is > 0)
        {
            return "OverkillCandidate";
        }
        if (!string.IsNullOrWhiteSpace(item.OwnerId))
        {
            return "PetOwnerAttribution";
        }
        if (correlation.Status == ParityCorrelationStatus.UnmatchedNormalized)
        {
            return "ParserNormalizationCandidate";
        }
        return ledger?.Decision == ParityLedgerDecision.Excluded
            ? $"LedgerExcluded/{ledger.ExclusionReason}"
            : "DirectDamage";
    }

    private static bool HasCompleteIdentity(ParityReplayEvent item)
        => HasIdentity(item.SourceId, item.SourceName) &&
           HasIdentity(item.TargetId, item.TargetName) &&
           HasIdentity(item.AbilityId, item.AbilityName);

    private static bool EquivalentSource(ParityReplayEvent left, ParityReplayEvent right)
        => EquivalentIdentity(left.SourceId, left.SourceName, right.SourceId, right.SourceName) ||
           EquivalentIdentity(left.OwnerId, string.Empty, right.SourceId, right.SourceName) ||
           EquivalentIdentity(left.SourceId, left.SourceName, right.OwnerId, string.Empty) ||
           EquivalentIdentity(left.OwnerId, string.Empty, right.OwnerId, string.Empty);

    private static bool EquivalentAbility(ParityReplayEvent left, ParityReplayEvent right)
        => EquivalentIdentity(left.AbilityId, left.AbilityName, right.AbilityId, right.AbilityName);

    private static bool EquivalentIdentity(
        string leftId,
        string leftName,
        string rightId,
        string rightName)
    {
        if (!string.IsNullOrWhiteSpace(leftId) && !string.IsNullOrWhiteSpace(rightId))
        {
            return string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase);
        }
        return !string.IsNullOrWhiteSpace(leftName) &&
               !string.IsNullOrWhiteSpace(rightName) &&
               string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasIdentity(string id, string name)
        => !string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(name);

    private static string BuildCorrelationId(PendingCorrelation item)
        => $"raw:{FirstNonEmpty(item.Raw?.RawEventId, item.Raw?.Sequence.ToString(), "none")}" +
           $":target-{item.Raw?.TargetIndex?.ToString() ?? "none"}" +
           $":effect-{item.Raw?.EffectIndex?.ToString() ?? "none"}" +
           $"/normalized:{FirstNonEmpty(item.Normalized?.ParserEventId, item.Normalized?.Sequence.ToString(), "none")}";

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private sealed record PendingCorrelation(
        ParityReplayEvent? Raw,
        ParityReplayEvent? Normalized,
        ParityCorrelationStatus Status,
        string Reason,
        int CandidateCount);

    private static long? EstimatedOverkill(this ParityReplayEvent item)
        => item.Overkill ?? (item.TargetCurrentHp is >= 0 && item.Amount > item.TargetCurrentHp
            ? item.Amount - item.TargetCurrentHp.Value
            : null);
}
