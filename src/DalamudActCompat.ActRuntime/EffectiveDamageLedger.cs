using Advanced_Combat_Tracker;
using System.Globalization;

namespace DalamudActCompat.ActRuntime;

/// <summary>
/// Builds FFLogs-compatible effective damage from confirmed network effects and
/// the parser's per-source periodic normalization.
/// </summary>
internal sealed class EffectiveDamageLedger
{
    private static readonly TimeSpan NormalizedCandidateLifetime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PreEncounterRawLineLifetime = TimeSpan.FromSeconds(2);
    private readonly object syncRoot = new();
    private readonly HashSet<string> partyActorIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> ownerIdsByActorId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ActionTargetKey, Queue<PendingActionDamage>> pendingActions = [];
    private readonly List<PendingPeriodicDamage> pendingPeriodicEffects = [];
    private readonly List<NormalizedDamageCandidate> normalizedCandidates = [];
    private readonly List<BufferedRawLine> preEncounterRawLines = [];
    private readonly Dictionary<string, long> targetEffectiveHp =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> sourceDamage =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> ownerDamage =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EffectiveDamageEvent> committedEvents = [];
    private bool encounterActive;

    public void StartEncounter(IReadOnlyList<ActPlayerIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        lock (syncRoot)
        {
            pendingActions.Clear();
            pendingPeriodicEffects.Clear();
            normalizedCandidates.Clear();
            targetEffectiveHp.Clear();
            sourceDamage.Clear();
            ownerDamage.Clear();
            committedEvents.Clear();
            partyActorIds.Clear();
            UpdatePartyActorsUnsafe(identities);
            encounterActive = true;
            foreach (var buffered in preEncounterRawLines)
            {
                ObserveActiveRawLineUnsafe(
                    buffered.Timestamp,
                    buffered.RawLine.TrimEnd('\r', '\n').Split('|'));
            }
            preEncounterRawLines.Clear();
        }
    }

    public void FinishEncounter()
    {
        lock (syncRoot)
        {
            encounterActive = false;
            FlushPeriodicEffectsUnsafe(DateTimeOffset.MaxValue);
            pendingActions.Clear();
            pendingPeriodicEffects.Clear();
            normalizedCandidates.Clear();
            preEncounterRawLines.Clear();
            targetEffectiveHp.Clear();
        }
    }

    public void PrepareFinalSnapshot()
    {
        lock (syncRoot)
        {
            FlushPeriodicEffectsUnsafe(DateTimeOffset.MaxValue);
        }
    }

    public void Reset()
    {
        lock (syncRoot)
        {
            encounterActive = false;
            partyActorIds.Clear();
            ownerIdsByActorId.Clear();
            pendingActions.Clear();
            pendingPeriodicEffects.Clear();
            normalizedCandidates.Clear();
            preEncounterRawLines.Clear();
            targetEffectiveHp.Clear();
            sourceDamage.Clear();
            ownerDamage.Clear();
            committedEvents.Clear();
        }
    }

    public void ObserveCombatAction(
        MasterSwing swing,
        IReadOnlyList<ActPlayerIdentity> identities,
        ActPlayerIdentity? attackerIdentity,
        ActPlayerIdentity? victimIdentity)
    {
        ArgumentNullException.ThrowIfNull(swing);
        var sourceId = ResolveTagActorId(swing, "SourceId");
        var targetId = ResolveTagActorId(swing, "TargetId");
        var ownerId = attackerIdentity is not null &&
                      !string.Equals(swing.Attacker, attackerIdentity.Name, StringComparison.OrdinalIgnoreCase) &&
                      !string.Equals(swing.Attacker, attackerIdentity.DisplayName, StringComparison.OrdinalIgnoreCase)
            ? FormatActorId(attackerIdentity.EntityId)
            : string.Empty;
        ObserveNormalizedDamage(new NormalizedDamageCandidate(
            swing.Time == default ? DateTimeOffset.Now : new DateTimeOffset(swing.Time),
            sourceId,
            swing.Attacker,
            ownerId,
            targetId,
            swing.Victim,
            swing.AttackType,
            (long)swing.Damage,
            RaidDpsEstimator.IsDamageSwing(swing),
            attackerIdentity is not null,
            victimIdentity is not null,
            swing.Critical,
            swing.Tags.TryGetValue("DirectHit", out var directHit) &&
            string.Equals(directHit?.ToString(), "True", StringComparison.OrdinalIgnoreCase)),
            identities);
    }

    internal void ObserveNormalizedDamage(
        NormalizedDamageCandidate item,
        IReadOnlyList<ActPlayerIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        lock (syncRoot)
        {
            if (!encounterActive)
            {
                return;
            }
            UpdatePartyActorsUnsafe(identities);
            item = ResolveNormalizedSourceIdentity(item, identities);
            PruneNormalizedCandidatesUnsafe(item.Timestamp);
            if (item.IsDamageSwing || item.Amount <= 0 || !item.IsPartyOwned || item.IsPartyTarget)
            {
                return;
            }
            if (pendingActions.Values
                .SelectMany(static queue => queue)
                .Any(action => MatchesAction(item, action)))
            {
                return;
            }
            normalizedCandidates.Add(item);
        }
    }

    public bool ObserveRawLine(DateTimeOffset timestamp, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        var fields = rawLine.TrimEnd('\r', '\n').Split('|');
        if (fields.Length < 2)
        {
            return false;
        }

        lock (syncRoot)
        {
            if (fields[0] == "03")
            {
                var changed = encounterActive && FlushPeriodicEffectsUnsafe(timestamp);
                ObserveActorUnsafe(fields);
                return changed;
            }
            if (!encounterActive)
            {
                if (fields[0] is "21" or "22" or "24" or "37")
                {
                    // The raw action callback precedes the first MasterSwing that creates
                    // an ACT encounter, so retain only the tiny window needed to replay it.
                    preEncounterRawLines.RemoveAll(
                        item => timestamp - item.Timestamp > PreEncounterRawLineLifetime);
                    preEncounterRawLines.Add(new BufferedRawLine(timestamp, rawLine));
                }
                return false;
            }
            return ObserveActiveRawLineUnsafe(timestamp, fields);
        }
    }

    public bool TryResolveDamage(ActPlayerIdentity identity, out long damage)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (syncRoot)
        {
            damage = ownerDamage.GetValueOrDefault(FormatActorId(identity.EntityId));
            return encounterActive;
        }
    }

    internal EffectiveDamageLedgerSnapshot GetSnapshot()
    {
        lock (syncRoot)
        {
            FlushPeriodicEffectsUnsafe(DateTimeOffset.MaxValue);
            return new EffectiveDamageLedgerSnapshot(
                sourceDamage.Values.Sum(),
                ownerDamage.Values.Sum(),
                new Dictionary<string, long>(sourceDamage, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, long>(ownerDamage, StringComparer.OrdinalIgnoreCase));
        }
    }

    internal IReadOnlyList<EffectiveDamageEvent> GetCommittedEventsSince(
        int startIndex,
        out int nextIndex)
    {
        lock (syncRoot)
        {
            startIndex = Math.Clamp(startIndex, 0, committedEvents.Count);
            nextIndex = committedEvents.Count;
            return committedEvents.Skip(startIndex).ToArray();
        }
    }

    private void ObserveActorUnsafe(IReadOnlyList<string> fields)
    {
        if (fields.Count < 7 || !IsActorId(fields[2]))
        {
            return;
        }
        var actorId = NormalizeActorId(fields[2]);
        if (IsActorId(fields[6]))
        {
            ownerIdsByActorId[actorId] = NormalizeActorId(fields[6]);
        }
        else
        {
            ownerIdsByActorId.Remove(actorId);
        }
    }

    private bool ObserveActiveRawLineUnsafe(DateTimeOffset timestamp, IReadOnlyList<string> fields)
    {
        var periodicChanged = FlushPeriodicEffectsUnsafe(timestamp);
        PruneNormalizedCandidatesUnsafe(timestamp);
        var lineChanged = fields[0] switch
        {
            "21" or "22" => ObserveActionEffectUnsafe(timestamp, fields),
            "24" => ObservePeriodicEffectUnsafe(timestamp, fields),
            "37" => ObserveEffectResultUnsafe(fields),
            _ => false,
        };
        return periodicChanged || lineChanged;
    }

    private bool ObserveActionEffectUnsafe(DateTimeOffset timestamp, IReadOnlyList<string> fields)
    {
        if (fields.Count < 46)
        {
            return false;
        }

        var sourceId = NormalizeActorId(fields[2]);
        var targetId = NormalizeActorId(fields[6]);
        var ownerId = fields.Count > 46 && IsActorId(fields[46])
            ? NormalizeActorId(fields[46])
            : ownerIdsByActorId.GetValueOrDefault(sourceId, string.Empty);
        if (!IsPartyOwnedUnsafe(sourceId, ownerId))
        {
            return false;
        }

        var packetId = NormalizePacketId(fields[44]);
        var key = new ActionTargetKey(packetId, targetId);
        if (!pendingActions.TryGetValue(key, out var queue))
        {
            queue = new Queue<PendingActionDamage>();
            pendingActions.Add(key, queue);
        }

        for (var effectIndex = 8; effectIndex < 24; effectIndex += 2)
        {
            if (!FfxivActionEffectDecoder.TryDecodeDamage(
                    fields[effectIndex],
                    fields[effectIndex + 1],
                    out var amount,
                    out var critical,
                    out var directHit) || amount <= 0)
            {
                continue;
            }

            var pending = new PendingActionDamage(
                timestamp,
                sourceId,
                fields[3],
                ownerId,
                targetId,
                fields[7],
                fields[5],
                amount,
                ParseDecimalLong(fields[24]),
                critical,
                directHit);
            queue.Enqueue(pending);
            RemoveMatchingNormalizedCandidateUnsafe(pending);
        }

        if (queue.Count == 0)
        {
            pendingActions.Remove(key);
        }
        return false;
    }

    private bool ObserveEffectResultUnsafe(IReadOnlyList<string> fields)
    {
        if (fields.Count < 6 ||
            !long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var resultHp))
        {
            return false;
        }

        var key = new ActionTargetKey(
            NormalizePacketId(fields[4]),
            NormalizeActorId(fields[2]));
        if (!pendingActions.TryGetValue(key, out var queue) || queue.Count == 0)
        {
            return false;
        }

        var item = queue.Dequeue();
        if (queue.Count == 0)
        {
            pendingActions.Remove(key);
        }

        var currentHp = ResolveTargetEffectiveHpUnsafe(item.TargetId, item.TargetCurrentHp);
        var effectiveDamage = resultHp <= 1
            ? Math.Min(item.Amount, Math.Max(0, currentHp))
            : item.Amount;
        // A one-HP boss floor has no remaining effective-damage budget. Keeping
        // the locally derived zero prevents later actions from receiving that HP twice.
        targetEffectiveHp[item.TargetId] = resultHp <= 1
            ? Math.Max(0, currentHp - effectiveDamage)
            : resultHp;
        AddDamageUnsafe(new EffectiveDamageEvent(
            item.Timestamp,
            item.SourceId,
            item.SourceName,
            item.OwnerId,
            item.TargetId,
            item.TargetName,
            item.AbilityName,
            effectiveDamage,
            item.Critical,
            item.DirectHit,
            IsPeriodic: false));
        return effectiveDamage > 0;
    }

    private bool ObservePeriodicEffectUnsafe(DateTimeOffset timestamp, IReadOnlyList<string> fields)
    {
        if (fields.Count < 20 || !string.Equals(fields[4], "DoT", StringComparison.Ordinal) ||
            !long.TryParse(fields[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawAmount))
        {
            return false;
        }

        var targetId = NormalizeActorId(fields[2]);
        var targetName = fields[3];
        var targetCurrentHp = ParseDecimalLong(fields[7]);
        var currentHp = ResolveTargetEffectiveHpUnsafe(targetId, targetCurrentHp);
        var rawEffectiveDamage = Math.Min(Math.Max(0, rawAmount), Math.Max(0, currentHp));
        targetEffectiveHp[targetId] = Math.Max(0, currentHp - rawEffectiveDamage);

        pendingPeriodicEffects.Add(new PendingPeriodicDamage(
            timestamp,
            targetId,
            targetName,
            rawAmount,
            rawEffectiveDamage));
        return rawEffectiveDamage > 0;
    }

    private bool FlushPeriodicEffectsUnsafe(DateTimeOffset beforeTimestamp)
    {
        var force = beforeTimestamp == DateTimeOffset.MaxValue;
        var pending = pendingPeriodicEffects
            .Where(item => force || beforeTimestamp - item.Timestamp > NormalizedCandidateLifetime)
            .ToArray();
        var changed = false;
        foreach (var item in pending)
        {
            pendingPeriodicEffects.Remove(item);
            changed |= CommitPeriodicEffectUnsafe(item);
        }
        return changed;
    }

    private bool CommitPeriodicEffectUnsafe(PendingPeriodicDamage periodic)
    {
        var candidates = normalizedCandidates
            .Where(item =>
                item.Timestamp == periodic.Timestamp &&
                EquivalentActor(
                    item.TargetId,
                    item.TargetName,
                    periodic.TargetId,
                    periodic.TargetName))
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }
        foreach (var candidate in candidates)
        {
            normalizedCandidates.Remove(candidate);
        }

        var normalizedTotal = candidates.Sum(static item => Math.Max(0, item.Amount));
        if (normalizedTotal <= 0 || periodic.RawEffectiveDamage <= 0)
        {
            return false;
        }
        var effectiveNormalizedTotal = periodic.RawEffectiveDamage >= periodic.RawAmount ||
                                       periodic.RawAmount <= 0
            ? normalizedTotal
            : (long)Math.Floor(
                (decimal)normalizedTotal * periodic.RawEffectiveDamage / periodic.RawAmount);
        var remainingNormalized = normalizedTotal;
        var remainingEffective = effectiveNormalizedTotal;
        foreach (var item in candidates)
        {
            var amount = Math.Max(0, item.Amount);
            // Allocate a partially overkilled aggregate tick proportionally while
            // preserving the exact group total across source and owner attribution.
            var effectiveAmount = remainingNormalized == amount
                ? remainingEffective
                : (long)Math.Floor((decimal)amount * effectiveNormalizedTotal / normalizedTotal);
            effectiveAmount = Math.Min(effectiveAmount, remainingEffective);
            AddDamageUnsafe(new EffectiveDamageEvent(
                item.Timestamp,
                item.SourceId,
                item.SourceName,
                item.OwnerId,
                item.TargetId,
                item.TargetName,
                item.AbilityName,
                effectiveAmount,
                item.Critical,
                item.DirectHit,
                IsPeriodic: true));
            remainingNormalized -= amount;
            remainingEffective -= effectiveAmount;
        }
        return effectiveNormalizedTotal > 0;
    }

    private void RemoveMatchingNormalizedCandidateUnsafe(PendingActionDamage action)
    {
        var candidate = normalizedCandidates.FirstOrDefault(item => MatchesAction(item, action));
        if (candidate is not null)
        {
            normalizedCandidates.Remove(candidate);
        }
    }

    private static bool MatchesAction(
        NormalizedDamageCandidate candidate,
        PendingActionDamage action)
        => candidate.Timestamp == action.Timestamp &&
           candidate.Amount == action.Amount &&
           EquivalentActor(
               candidate.SourceId,
               candidate.SourceName,
               action.SourceId,
               action.SourceName) &&
           EquivalentActor(
               candidate.TargetId,
               candidate.TargetName,
               action.TargetId,
               action.TargetName) &&
           string.Equals(
               candidate.AbilityName,
               action.AbilityName,
               StringComparison.OrdinalIgnoreCase);

    private long ResolveTargetEffectiveHpUnsafe(string targetId, long? observedCurrentHp)
    {
        if (targetEffectiveHp.TryGetValue(targetId, out var currentHp))
        {
            return currentHp;
        }
        currentHp = Math.Max(0, observedCurrentHp ?? 0);
        targetEffectiveHp[targetId] = currentHp;
        return currentHp;
    }

    private void AddDamageUnsafe(EffectiveDamageEvent item)
    {
        if (item.Amount <= 0)
        {
            return;
        }
        var sourceKey = ActorKey(item.SourceId, item.SourceName);
        var ownerKey = !string.IsNullOrWhiteSpace(item.OwnerId)
            ? NormalizeActorId(item.OwnerId)
            : sourceKey;
        sourceDamage[sourceKey] = sourceDamage.GetValueOrDefault(sourceKey) + item.Amount;
        ownerDamage[ownerKey] = ownerDamage.GetValueOrDefault(ownerKey) + item.Amount;
        // Downstream metrics consume this immutable decision instead of reclassifying
        // the parser swing and silently diverging from the authoritative totals.
        committedEvents.Add(item);
    }

    private void UpdatePartyActorsUnsafe(IReadOnlyList<ActPlayerIdentity> identities)
    {
        foreach (var identity in identities)
        {
            if (identity.EntityId == 0)
            {
                continue;
            }
            var actorId = FormatActorId(identity.EntityId);
            partyActorIds.Add(actorId);
        }
    }

    private bool IsPartyOwnedUnsafe(string sourceId, string ownerId)
        => partyActorIds.Contains(sourceId) ||
           !string.IsNullOrWhiteSpace(ownerId) && partyActorIds.Contains(ownerId);

    private static NormalizedDamageCandidate ResolveNormalizedSourceIdentity(
        NormalizedDamageCandidate item,
        IReadOnlyList<ActPlayerIdentity> identities)
    {
        if (!string.IsNullOrWhiteSpace(item.SourceId))
        {
            return item;
        }

        var identity = ActPlayerIdentityResolver.Resolve(identities, item.SourceName);
        if (identity?.EntityId is null or 0)
        {
            return item;
        }

        // Simulated periodic swings can omit SourceId even though ACT has already
        // resolved the player name. Use the party identity so owner totals remain
        // addressable by the entity ID consumed by TryResolveDamage.
        return item with { SourceId = FormatActorId(identity.EntityId) };
    }

    private void PruneNormalizedCandidatesUnsafe(DateTimeOffset timestamp)
        => normalizedCandidates.RemoveAll(item => timestamp - item.Timestamp > NormalizedCandidateLifetime);

    private static bool EquivalentActor(
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

    private static long? ParseDecimalLong(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string ResolveTagActorId(MasterSwing swing, string key)
        => swing.Tags.TryGetValue(key, out var value)
            ? NormalizeActorId(value?.ToString())
            : string.Empty;

    private static bool IsActorId(string value)
        => value.Length == 8 && uint.TryParse(
            value,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var actorId) && actorId != 0;

    private static string ActorKey(string actorId, string actorName)
        => !string.IsNullOrWhiteSpace(actorId)
            ? NormalizeActorId(actorId)
            : $"name:{actorName.Trim().ToUpperInvariant()}";

    private static string NormalizeActorId(string? value)
        => uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var actorId) && actorId != 0
            ? FormatActorId(actorId)
            : string.Empty;

    private static string NormalizePacketId(string value)
        => value.Trim().ToUpperInvariant();

    private static string FormatActorId(uint actorId)
        => actorId == 0 ? string.Empty : actorId.ToString("X8", CultureInfo.InvariantCulture);

    private readonly record struct ActionTargetKey(string PacketId, string TargetId);

    private sealed record BufferedRawLine(DateTimeOffset Timestamp, string RawLine);

    private sealed record PendingActionDamage(
        DateTimeOffset Timestamp,
        string SourceId,
        string SourceName,
        string OwnerId,
        string TargetId,
        string TargetName,
        string AbilityName,
        long Amount,
        long? TargetCurrentHp,
        bool Critical,
        bool DirectHit);

    private sealed record PendingPeriodicDamage(
        DateTimeOffset Timestamp,
        string TargetId,
        string TargetName,
        long RawAmount,
        long RawEffectiveDamage);
}

internal sealed record NormalizedDamageCandidate(
    DateTimeOffset Timestamp,
    string SourceId,
    string SourceName,
    string OwnerId,
    string TargetId,
    string TargetName,
    string AbilityName,
    long Amount,
    bool IsDamageSwing,
    bool IsPartyOwned,
    bool IsPartyTarget,
    bool Critical = false,
    bool DirectHit = false);

internal sealed record EffectiveDamageEvent(
    DateTimeOffset Timestamp,
    string SourceId,
    string SourceName,
    string OwnerId,
    string TargetId,
    string TargetName,
    string AbilityName,
    long Amount,
    bool Critical,
    bool DirectHit,
    bool IsPeriodic);

internal sealed record EffectiveDamageLedgerSnapshot(
    long SourceDamage,
    long OwnerDamage,
    IReadOnlyDictionary<string, long> SourceTotals,
    IReadOnlyDictionary<string, long> OwnerTotals);
