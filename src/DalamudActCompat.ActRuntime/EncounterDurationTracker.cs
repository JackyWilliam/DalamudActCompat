using System.Globalization;

namespace DalamudActCompat.ActRuntime;

/// <summary>
/// Separates encounter wall time, global damage-metric time, and per-actor
/// observed activity from confirmed network damage events.
/// </summary>
internal sealed class EncounterDurationTracker
{
    private static readonly TimeSpan PreEncounterRawLineLifetime = TimeSpan.FromSeconds(2);
    private readonly object syncRoot = new();
    private readonly HashSet<string> partyActorIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActorPresence> actors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> ownerIdsByActorId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ActionTargetKey, Queue<PendingDamage>> pendingActions = [];
    private readonly List<BufferedRawLine> preEncounterRawLines = [];
    private readonly Dictionary<string, TargetTimeline> targets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActivitySpan> actorActivity =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset encounterStart;
    private DateTimeOffset firstConfirmedDamage;
    private DateTimeOffset lastConfirmedDamage;
    private bool encounterActive;

    public void StartEncounter(
        DateTimeOffset startTime,
        IReadOnlyList<ActPlayerIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        lock (syncRoot)
        {
            pendingActions.Clear();
            targets.Clear();
            actorActivity.Clear();
            partyActorIds.Clear();
            UpdatePartyActorsUnsafe(identities);
            encounterStart = startTime;
            firstConfirmedDamage = default;
            lastConfirmedDamage = default;
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
            pendingActions.Clear();
            preEncounterRawLines.Clear();
        }
    }

    public void Reset()
    {
        lock (syncRoot)
        {
            encounterActive = false;
            partyActorIds.Clear();
            actors.Clear();
            ownerIdsByActorId.Clear();
            pendingActions.Clear();
            preEncounterRawLines.Clear();
            targets.Clear();
            actorActivity.Clear();
            encounterStart = default;
            firstConfirmedDamage = default;
            lastConfirmedDamage = default;
        }
    }

    public bool ObserveRawLine(
        DateTimeOffset timestamp,
        string rawLine,
        IReadOnlyList<ActPlayerIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
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
            UpdatePartyActorsUnsafe(identities);
            if (fields[0] == "03")
            {
                ObserveActorUnsafe(timestamp, fields);
                return false;
            }
            if (fields[0] == "261")
            {
                ObserveActorPresenceUnsafe(timestamp, fields);
                return false;
            }
            if (!encounterActive)
            {
                if (fields[0] is "21" or "22" or "24" or "34" or "37")
                {
                    // Raw input precedes the first MasterSwing that creates the ACT
                    // encounter, so preserve only enough input to recover that boundary.
                    preEncounterRawLines.RemoveAll(
                        item => timestamp - item.Timestamp > PreEncounterRawLineLifetime);
                    preEncounterRawLines.Add(new BufferedRawLine(timestamp, rawLine));
                }
                return false;
            }
            return ObserveActiveRawLineUnsafe(timestamp, fields);
        }
    }

    public double ResolveFightDurationSeconds(DateTimeOffset measurementEndTime)
    {
        lock (syncRoot)
        {
            return encounterStart != default && measurementEndTime > encounterStart
                ? (measurementEndTime - encounterStart).TotalSeconds
                : 0;
        }
    }

    public double ResolveDamageMetricDurationSeconds(
        DateTimeOffset measurementEndTime,
        bool useObservedDamageEnd)
    {
        lock (syncRoot)
        {
            var endTime = useObservedDamageEnd ? lastConfirmedDamage : measurementEndTime;
            if (firstConfirmedDamage == default || endTime <= firstConfirmedDamage)
            {
                return 0;
            }
            return Math.Max(
                0,
                (endTime - firstConfirmedDamage).TotalSeconds -
                ResolveGlobalDowntimeSecondsUnsafe(firstConfirmedDamage, endTime));
        }
    }

    public double ResolveActorActiveTimeSeconds(string actorId)
    {
        lock (syncRoot)
        {
            var key = NormalizeActorId(actorId);
            return actorActivity.TryGetValue(key, out var activity) &&
                   activity.LastDamage > activity.FirstDamage
                ? (activity.LastDamage - activity.FirstDamage).TotalSeconds
                : 0;
        }
    }

    internal bool IsTransitioningAt(DateTimeOffset timestamp)
    {
        lock (syncRoot)
        {
            if (!encounterActive)
            {
                return false;
            }
            var intervals = BuildMembershipIntervalsUnsafe(timestamp.AddTicks(1))
                .Where(interval => interval.Start <= timestamp && timestamp < interval.End)
                .ToArray();
            return intervals.Length > 0 && intervals.All(interval =>
                !IsTargetableAtUnsafe(targets[interval.TargetKey], interval, timestamp));
        }
    }

    internal void ObserveTargetPresence(
        DateTimeOffset timestamp,
        string targetId,
        string targetName)
    {
        lock (syncRoot)
        {
            RememberActorPresenceUnsafe(timestamp, targetId, targetName);
        }
    }

    internal void ObserveTargetability(
        DateTimeOffset timestamp,
        string targetId,
        string targetName,
        bool targetable)
    {
        lock (syncRoot)
        {
            ObserveTargetabilityUnsafe(timestamp, targetId, targetName, targetable);
        }
    }

    internal void ObserveConfirmedDamage(
        DateTimeOffset timestamp,
        string sourceId,
        string sourceName,
        string ownerId,
        string targetId,
        string targetName)
    {
        lock (syncRoot)
        {
            RecordConfirmedDamageUnsafe(
                timestamp,
                sourceId,
                sourceName,
                ownerId,
                targetId,
                targetName);
        }
    }

    private bool ObserveActiveRawLineUnsafe(
        DateTimeOffset timestamp,
        IReadOnlyList<string> fields)
        => fields[0] switch
        {
            "21" or "22" => ObserveActionEffectUnsafe(timestamp, fields),
            "24" => ObservePeriodicEffectUnsafe(timestamp, fields),
            "25" => ObserveDeathUnsafe(timestamp, fields),
            "34" => ObserveTargetabilityLineUnsafe(timestamp, fields),
            "37" => ObserveEffectResultUnsafe(timestamp, fields),
            _ => false,
        };

    private void ObserveActorUnsafe(DateTimeOffset timestamp, IReadOnlyList<string> fields)
    {
        if (fields.Count < 7 || !IsActorId(fields[2]))
        {
            return;
        }
        RememberActorPresenceUnsafe(timestamp, fields[2], fields[3]);
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

    private void ObserveActorPresenceUnsafe(
        DateTimeOffset timestamp,
        IReadOnlyList<string> fields)
    {
        if (fields.Count < 4 || !IsActorId(fields[3]))
        {
            return;
        }
        var actorId = NormalizeActorId(fields[3]);
        if (string.Equals(fields[2], "Remove", StringComparison.OrdinalIgnoreCase))
        {
            if (actors.TryGetValue(actorId, out var actor))
            {
                actor.RemovedAt = timestamp;
            }
            if (targets.TryGetValue(actorId, out var target))
            {
                target.RemovedAt = timestamp;
            }
            return;
        }
        if (!string.Equals(fields[2], "Add", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var nameIndex = Array.FindIndex(fields.ToArray(), static field =>
            string.Equals(field, "Name", StringComparison.OrdinalIgnoreCase));
        var actorName = nameIndex >= 0 && nameIndex + 1 < fields.Count
            ? fields[nameIndex + 1]
            : string.Empty;
        RememberActorPresenceUnsafe(timestamp, actorId, actorName);
    }

    private bool ObserveActionEffectUnsafe(
        DateTimeOffset timestamp,
        IReadOnlyList<string> fields)
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
        if (!IsPartyOwnedUnsafe(sourceId, ownerId) || partyActorIds.Contains(targetId))
        {
            return false;
        }

        var key = new ActionTargetKey(NormalizePacketId(fields[44]), targetId);
        if (!pendingActions.TryGetValue(key, out var queue))
        {
            queue = new Queue<PendingDamage>();
            pendingActions.Add(key, queue);
        }
        for (var effectIndex = 8; effectIndex < 24; effectIndex += 2)
        {
            if (!FfxivActionEffectDecoder.TryDecodeDamage(
                    fields[effectIndex],
                    fields[effectIndex + 1],
                    out var amount,
                    out _,
                    out _) || amount <= 0)
            {
                continue;
            }
            queue.Enqueue(new PendingDamage(
                sourceId,
                fields[3],
                ownerId,
                targetId,
                fields[7],
                amount,
                ParseDecimalLong(fields[24])));
        }
        if (queue.Count == 0)
        {
            pendingActions.Remove(key);
        }
        return false;
    }

    private bool ObserveEffectResultUnsafe(
        DateTimeOffset timestamp,
        IReadOnlyList<string> fields)
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
        var effectiveDamage = resultHp <= 1
            ? Math.Min(item.Amount, Math.Max(0, item.TargetCurrentHp ?? 0))
            : item.Amount;
        if (effectiveDamage <= 0)
        {
            return false;
        }
        // FFLogs timestamps an accepted direct effect when its target-specific
        // EffectResult arrives, not when the tentative ActionEffect was emitted.
        RecordConfirmedDamageUnsafe(
            timestamp,
            item.SourceId,
            item.SourceName,
            item.OwnerId,
            item.TargetId,
            item.TargetName);
        return true;
    }

    private bool ObservePeriodicEffectUnsafe(
        DateTimeOffset timestamp,
        IReadOnlyList<string> fields)
    {
        if (fields.Count < 19 ||
            !string.Equals(fields[4], "DoT", StringComparison.Ordinal) ||
            !long.TryParse(fields[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var amount) ||
            amount <= 0)
        {
            return false;
        }
        var sourceId = NormalizeActorId(fields[17]);
        var ownerId = ownerIdsByActorId.GetValueOrDefault(sourceId, string.Empty);
        var targetId = NormalizeActorId(fields[2]);
        var targetCurrentHp = ParseDecimalLong(fields[7]);
        if (!IsPartyOwnedUnsafe(sourceId, ownerId) ||
            partyActorIds.Contains(targetId) ||
            targetCurrentHp is <= 1)
        {
            return false;
        }
        RecordConfirmedDamageUnsafe(
            timestamp,
            sourceId,
            fields[18],
            ownerId,
            targetId,
            fields[3]);
        return true;
    }

    private bool ObserveDeathUnsafe(DateTimeOffset timestamp, IReadOnlyList<string> fields)
    {
        if (fields.Count < 4 || !IsActorId(fields[2]))
        {
            return false;
        }
        var targetId = NormalizeActorId(fields[2]);
        if (partyActorIds.Contains(targetId) ||
            !targets.TryGetValue(targetId, out var target) ||
            target.MembershipStart == default)
        {
            return false;
        }
        if (target.DefeatedAt is not null && target.DefeatedAt <= timestamp)
        {
            return false;
        }

        // HP floors are encounter mechanics as well as health values. Only the
        // explicit network death line is reliable enough to retire target membership.
        target.DefeatedAt = timestamp;
        return true;
    }

    private bool ObserveTargetabilityLineUnsafe(
        DateTimeOffset timestamp,
        IReadOnlyList<string> fields)
    {
        if (fields.Count < 7 || (fields[6] != "00" && fields[6] != "01"))
        {
            return false;
        }
        return ObserveTargetabilityUnsafe(
            timestamp,
            fields[2],
            fields[3],
            fields[6] == "01");
    }

    private bool ObserveTargetabilityUnsafe(
        DateTimeOffset timestamp,
        string targetId,
        string targetName,
        bool targetable)
    {
        var key = ActorKey(targetId, targetName);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        if (!targets.TryGetValue(key, out var target))
        {
            target = new TargetTimeline(key);
            targets.Add(key, target);
        }
        var previous = target.Targetability.Count == 0 || target.Targetability[^1].Targetable;
        target.Targetability.Add(new TargetabilityChange(timestamp, targetable));
        return previous != targetable;
    }

    private void RecordConfirmedDamageUnsafe(
        DateTimeOffset timestamp,
        string sourceId,
        string sourceName,
        string ownerId,
        string targetId,
        string targetName)
    {
        if (!encounterActive)
        {
            return;
        }
        if (firstConfirmedDamage == default || timestamp < firstConfirmedDamage)
        {
            firstConfirmedDamage = timestamp;
        }
        if (lastConfirmedDamage == default || timestamp > lastConfirmedDamage)
        {
            lastConfirmedDamage = timestamp;
        }

        var targetKey = ActorKey(targetId, targetName);
        if (!targets.TryGetValue(targetKey, out var target))
        {
            target = new TargetTimeline(targetKey);
            targets.Add(targetKey, target);
        }
        var presenceStart = timestamp;
        if (actors.TryGetValue(targetKey, out var presence))
        {
            if (presence.AddedAt <= encounterStart)
            {
                // Targets already present at pull can belong to the same multi-boss
                // phase even when their first individual damage occurs much later.
                presenceStart = encounterStart;
            }
            else
            {
                presenceStart = target.Targetability
                    .Where(change =>
                        change.Targetable &&
                        change.Timestamp >= presence.AddedAt &&
                        change.Timestamp <= timestamp)
                    .Select(static change => change.Timestamp)
                    .DefaultIfEmpty(timestamp)
                    .Min();
            }
        }
        if (target.MembershipStart == default || presenceStart < target.MembershipStart)
        {
            target.MembershipStart = presenceStart;
        }
        target.LastDamage = timestamp;

        var activityKey = !string.IsNullOrWhiteSpace(ownerId)
            ? NormalizeActorId(ownerId)
            : ActorKey(sourceId, sourceName);
        if (!actorActivity.TryGetValue(activityKey, out var activity))
        {
            actorActivity.Add(activityKey, new ActivitySpan(timestamp, timestamp));
        }
        else
        {
            activity.FirstDamage = timestamp < activity.FirstDamage ? timestamp : activity.FirstDamage;
            activity.LastDamage = timestamp > activity.LastDamage ? timestamp : activity.LastDamage;
        }
    }

    private double ResolveGlobalDowntimeSecondsUnsafe(
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var intervals = BuildMembershipIntervalsUnsafe(rangeEnd);
        var points = intervals
            .SelectMany(static interval => new[] { interval.Start, interval.End })
            .Concat(targets.Values.SelectMany(static target =>
                target.Targetability.Select(static change => change.Timestamp)))
            .Append(rangeStart)
            .Append(rangeEnd)
            .Where(timestamp => timestamp >= rangeStart && timestamp <= rangeEnd)
            .Distinct()
            .OrderBy(static timestamp => timestamp)
            .ToArray();
        var downtime = 0d;
        for (var index = 0; index + 1 < points.Length; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            if (end <= start)
            {
                continue;
            }
            var active = intervals
                .Where(interval => interval.Start <= start && start < interval.End)
                .ToArray();
            if (active.Length > 0 && active.All(interval =>
                    !IsTargetableAtUnsafe(targets[interval.TargetKey], interval, start)))
            {
                downtime += (end - start).TotalSeconds;
            }
        }
        return downtime;
    }

    private IReadOnlyList<TargetMembershipInterval> BuildMembershipIntervalsUnsafe(
        DateTimeOffset rangeEnd)
    {
        var candidates = targets.Values
            .Where(static target => target.MembershipStart != default)
            .Select(target => new TargetMembershipCandidate(
                target.Key,
                target.MembershipStart,
                ResolveNaturalExitUnsafe(target, rangeEnd)))
            .ToArray();
        var result = new List<TargetMembershipInterval>(candidates.Length);
        foreach (var candidate in candidates)
        {
            var end = rangeEnd;
            if (candidate.NaturalExit is DateTimeOffset exit)
            {
                var phaseHasSurvivor = candidates.Any(other =>
                    !string.Equals(other.TargetKey, candidate.TargetKey, StringComparison.OrdinalIgnoreCase) &&
                    other.Start <= exit &&
                    (other.NaturalExit is null || other.NaturalExit > exit));
                if (phaseHasSurvivor)
                {
                    end = exit;
                }
                else
                {
                    // When an entire phase leaves, its unavailable targets remain the
                    // current phase until the replacement target set actually begins.
                    end = candidates
                        .Where(other => other.Start > exit)
                        .Select(static other => other.Start)
                        .DefaultIfEmpty(rangeEnd)
                        .Min();
                }
            }
            if (end > candidate.Start)
            {
                result.Add(new TargetMembershipInterval(
                    candidate.TargetKey,
                    candidate.Start,
                    end,
                    candidate.NaturalExit));
            }
        }
        return result;
    }

    private DateTimeOffset? ResolveNaturalExitUnsafe(
        TargetTimeline target,
        DateTimeOffset rangeEnd)
    {
        var explicitExit = new[] { target.DefeatedAt, target.RemovedAt }
            .Where(static timestamp => timestamp is not null)
            .Select(static timestamp => timestamp!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        var lastTargetability = target.Targetability
            .Where(change => change.Timestamp <= rangeEnd)
            .OrderBy(static change => change.Timestamp)
            .LastOrDefault();
        var permanentUntargetable = lastTargetability is { Targetable: false } &&
                                    target.LastDamage <= lastTargetability.Timestamp
            ? lastTargetability.Timestamp
            : DateTimeOffset.MaxValue;
        var exit = explicitExit < permanentUntargetable ? explicitExit : permanentUntargetable;
        return exit == DateTimeOffset.MaxValue ? null : exit;
    }

    private static bool IsTargetableAtUnsafe(
        TargetTimeline target,
        TargetMembershipInterval membership,
        DateTimeOffset timestamp)
    {
        if (membership.NaturalExit is DateTimeOffset exit && timestamp >= exit)
        {
            return false;
        }
        return target.Targetability
            .Where(change => change.Timestamp <= timestamp)
            .OrderBy(static change => change.Timestamp)
            .Select(static change => change.Targetable)
            .DefaultIfEmpty(true)
            .Last();
    }

    private void RememberActorPresenceUnsafe(
        DateTimeOffset timestamp,
        string actorId,
        string actorName)
    {
        var key = ActorKey(actorId, actorName);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        if (!actors.TryGetValue(key, out var actor) || actor.RemovedAt is not null)
        {
            actors[key] = new ActorPresence(timestamp);
        }
    }

    private void UpdatePartyActorsUnsafe(IReadOnlyList<ActPlayerIdentity> identities)
    {
        foreach (var identity in identities)
        {
            if (identity.EntityId != 0)
            {
                partyActorIds.Add(FormatActorId(identity.EntityId));
            }
        }
    }

    private bool IsPartyOwnedUnsafe(string sourceId, string ownerId)
        => partyActorIds.Contains(sourceId) ||
           !string.IsNullOrWhiteSpace(ownerId) && partyActorIds.Contains(ownerId);

    private static long? ParseDecimalLong(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static bool IsActorId(string value)
        => value.Length == 8 && uint.TryParse(
            value,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var actorId) && actorId != 0;

    private static string ActorKey(string actorId, string actorName)
        => !string.IsNullOrWhiteSpace(NormalizeActorId(actorId))
            ? NormalizeActorId(actorId)
            : string.IsNullOrWhiteSpace(actorName)
                ? string.Empty
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

    private sealed record PendingDamage(
        string SourceId,
        string SourceName,
        string OwnerId,
        string TargetId,
        string TargetName,
        long Amount,
        long? TargetCurrentHp);

    private sealed class ActorPresence(DateTimeOffset addedAt)
    {
        public DateTimeOffset AddedAt { get; } = addedAt;
        public DateTimeOffset? RemovedAt { get; set; }
    }

    private sealed class TargetTimeline(string key)
    {
        public string Key { get; } = key;
        public DateTimeOffset MembershipStart { get; set; }
        public DateTimeOffset LastDamage { get; set; }
        public DateTimeOffset? DefeatedAt { get; set; }
        public DateTimeOffset? RemovedAt { get; set; }
        public List<TargetabilityChange> Targetability { get; } = [];
    }

    private sealed class ActivitySpan(
        DateTimeOffset firstDamage,
        DateTimeOffset lastDamage)
    {
        public DateTimeOffset FirstDamage { get; set; } = firstDamage;
        public DateTimeOffset LastDamage { get; set; } = lastDamage;
    }

    private readonly record struct TargetabilityChange(
        DateTimeOffset Timestamp,
        bool Targetable);

    private readonly record struct TargetMembershipCandidate(
        string TargetKey,
        DateTimeOffset Start,
        DateTimeOffset? NaturalExit);

    private readonly record struct TargetMembershipInterval(
        string TargetKey,
        DateTimeOffset Start,
        DateTimeOffset End,
        DateTimeOffset? NaturalExit);
}
