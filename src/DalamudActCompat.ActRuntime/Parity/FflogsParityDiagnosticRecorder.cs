using Advanced_Combat_Tracker;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;

namespace DalamudActCompat.ActRuntime.Parity;

/// <summary>
/// Thread-safe sidecar recorder for the live ACT pipeline. It observes data but
/// never feeds values back into EncounterData, RaidDpsEstimator, or UI snapshots.
/// </summary>
internal sealed class FflogsParityDiagnosticRecorder
{
    private const int MaximumEventsPerLayer = 200_000;
    private readonly object syncRoot = new();
    private readonly FflogsParityRawNormalizer rawNormalizer = new([]);
    private readonly List<ParityReplayEvent> rawEvents = [];
    private readonly List<ParityReplayEvent> normalizedEvents = [];
    private readonly Dictionary<string, string> normalizedIdentitySignatures =
        new(StringComparer.OrdinalIgnoreCase);
    private long normalizedSequence;
    private bool ledgerTruncated;

    public void ObserveRawLine(string rawLine)
    {
        lock (syncRoot)
        {
            var events = rawNormalizer.Normalize(rawLine);
            if (events.Count == 0)
            {
                return;
            }
            AppendBounded(rawEvents, events);
        }
    }

    public void ObserveCombatAction(
        MasterSwing swing,
        IReadOnlyList<ActPlayerIdentity> identities,
        ActPlayerIdentity? attackerIdentity,
        bool hasEncounter)
    {
        ArgumentNullException.ThrowIfNull(swing);
        ArgumentNullException.ThrowIfNull(identities);
        var sourceId = ResolveTagActorId(swing, "SourceId");
        var targetId = ResolveTagActorId(swing, "TargetId");
        var ownerId = string.Empty;
        if (attackerIdentity is not null &&
            !string.Equals(swing.Attacker, attackerIdentity.Name, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(swing.Attacker, attackerIdentity.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            ownerId = FormatActorId(attackerIdentity.EntityId);
        }
        var sourceIdentity = attackerIdentity ?? ResolveIdentityById(identities, sourceId);
        var victimIdentity = ResolveIdentityById(identities, targetId) ??
                             ActPlayerIdentityResolver.Resolve(identities, swing.Victim);
        var amount = (long)swing.Damage;
        var isDamageSwing = RaidDpsEstimator.IsDamageSwing(swing);
        var actionTime = swing.Time == default
            ? DateTimeOffset.Now
            : new DateTimeOffset(swing.Time);
        var directHit = swing.Tags.TryGetValue("DirectHit", out var directValue) &&
                        string.Equals(
                            directValue?.ToString(),
                            "True",
                            StringComparison.OrdinalIgnoreCase);
        var kind = ResolveDamageKind(swing);

        lock (syncRoot)
        {
            if (normalizedEvents.Count >= MaximumEventsPerLayer)
            {
                ledgerTruncated = true;
                return;
            }

            normalizedEvents.Add(new ParityReplayEvent
            {
                Kind = ParityReplayEventKind.Damage,
                Timestamp = actionTime,
                Sequence = ++normalizedSequence,
                SourceId = sourceId,
                SourceName = swing.Attacker,
                Job = sourceIdentity?.Job ?? ResolveTagString(swing, "Job"),
                OwnerId = ownerId,
                TargetId = targetId,
                TargetName = swing.Victim,
                AbilityName = swing.AttackType,
                Amount = amount,
                Critical = swing.Critical,
                DirectHit = directHit,
                IsPartyMember = sourceIdentity is not null,
                IsDamageSwing = isDamageSwing,
                HasEncounter = hasEncounter,
                DamageKind = kind,
                RawLineType = "MasterSwing",
                PacketId = swing.TimeSorter.ToString(CultureInfo.InvariantCulture),
                Evidence = isDamageSwing
                    ? "FFXIV_ACT_Plugin normalized MasterSwing observed before DACT party filtering"
                    : "Non-damage MasterSwing retained so exclusion is explicit",
            });

            RememberIdentitiesUnsafe(identities, actionTime);
            if (victimIdentity is not null)
            {
                RememberIdentityUnsafe(victimIdentity, actionTime);
            }
        }
    }

    public FflogsParityDiagnostic Complete(
        Guid encounterId,
        string zone,
        string encounterName,
        DateTimeOffset? fightStart,
        DateTimeOffset? fightEnd,
        IReadOnlyList<ActPlayerIdentity> identities,
        long droppedRawLogLines,
        long droppedNormalizedActions,
        ParityReferenceSnapshot? reference = null)
    {
        ParityReplayEvent[] normalizedSnapshot;
        ParityReplayEvent[] rawSnapshot;
        bool truncated;
        lock (syncRoot)
        {
            RememberIdentitiesUnsafe(identities, fightStart ?? DateTimeOffset.Now);
            normalizedSnapshot = FilterForEncounter(normalizedEvents, fightStart, fightEnd);
            rawSnapshot = FilterForEncounter(rawEvents, fightStart, fightEnd);
            truncated = ledgerTruncated;
            normalizedEvents.Clear();
            rawEvents.Clear();
            normalizedSequence = 0;
            normalizedIdentitySignatures.Clear();
            ledgerTruncated = false;
        }

        var partyActorIds = identities
            .Where(static identity => identity.EntityId != 0)
            .Select(static identity => FormatActorId(identity.EntityId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parserAssembly = typeof(FFXIV_ACT_Plugin.FFXIV_ACT_Plugin).Assembly;
        var parserPath = parserAssembly.Location;
        var firstCapturedTimelineEvent = normalizedSnapshot
            .Concat(rawSnapshot)
            .Where(static item => item.Kind != ParityReplayEventKind.Actor)
            .Select(static item => (DateTimeOffset?)item.Timestamp)
            .Min();
        var partialCapture = fightStart is not null &&
                             (firstCapturedTimelineEvent is null ||
                              firstCapturedTimelineEvent > fightStart.Value.AddMilliseconds(100));
        var captureHealth = new ParityCaptureHealth(
            droppedRawLogLines,
            droppedNormalizedActions,
            truncated,
            partialCapture,
            rawSnapshot.Count(static item => item.Kind == ParityReplayEventKind.Damage),
            normalizedSnapshot.Count(static item => item.Kind == ParityReplayEventKind.Damage),
            parserAssembly.GetName().Version?.ToString() ?? "unknown",
            ResolveSha256(parserPath));
        return FflogsParityAnalyzer.Analyze(
            encounterId,
            zone,
            encounterName,
            fightStart,
            fightEnd,
            normalizedSnapshot,
            rawSnapshot,
            partyActorIds,
            captureHealth,
            reference);
    }

    public void ResetEncounter()
    {
        lock (syncRoot)
        {
            rawEvents.Clear();
            normalizedEvents.Clear();
            normalizedSequence = 0;
            normalizedIdentitySignatures.Clear();
            ledgerTruncated = false;
        }
    }

    private void AppendBounded(
        ICollection<ParityReplayEvent> destination,
        IReadOnlyList<ParityReplayEvent> values)
    {
        foreach (var value in values)
        {
            if (destination.Count >= MaximumEventsPerLayer)
            {
                ledgerTruncated = true;
                return;
            }
            destination.Add(value);
        }
    }

    private void RememberIdentitiesUnsafe(
        IReadOnlyList<ActPlayerIdentity> identities,
        DateTimeOffset timestamp)
    {
        foreach (var identity in identities)
        {
            RememberIdentityUnsafe(identity, timestamp);
        }
    }

    private void RememberIdentityUnsafe(ActPlayerIdentity identity, DateTimeOffset timestamp)
    {
        if (identity.EntityId == 0)
        {
            return;
        }

        var actorId = FormatActorId(identity.EntityId);
        var signature = $"{identity.DisplayName}\0{identity.Job}";
        if (normalizedIdentitySignatures.TryGetValue(actorId, out var existingSignature) &&
            string.Equals(existingSignature, signature, StringComparison.Ordinal))
        {
            return;
        }
        if (normalizedEvents.Count >= MaximumEventsPerLayer)
        {
            ledgerTruncated = true;
            return;
        }

        normalizedEvents.Add(new ParityReplayEvent
        {
            Kind = ParityReplayEventKind.Actor,
            Timestamp = timestamp,
            Sequence = ++normalizedSequence,
            SourceId = actorId,
            SourceName = identity.DisplayName,
            Job = identity.Job,
            IsPartyMember = true,
            RawLineType = "DalamudParty",
            Evidence = "Dalamud party identity snapshot",
        });
        normalizedIdentitySignatures[actorId] = signature;
    }

    private static ParityReplayEvent[] FilterForEncounter(
        IEnumerable<ParityReplayEvent> source,
        DateTimeOffset? start,
        DateTimeOffset? end)
    {
        var actorEvents = source.Where(static item => item.Kind == ParityReplayEventKind.Actor);
        var timelineEvents = source.Where(item =>
            item.Kind != ParityReplayEventKind.Actor &&
            (start is null || item.Timestamp >= start) &&
            (end is null || item.Timestamp <= end));
        // Actor metadata may be emitted before the pull and is required to resolve
        // pets during replay, so retain the latest observation for each actor ID.
        return actorEvents
            .GroupBy(static item => item.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Last())
            .Concat(timelineEvents)
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => item.Sequence)
            .ToArray();
    }

    private static string ResolveTagActorId(MasterSwing swing, string key)
        => swing.Tags.TryGetValue(key, out var value)
            ? NormalizeActorId(value?.ToString())
            : string.Empty;

    private static string ResolveTagString(MasterSwing swing, string key)
        => swing.Tags.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    private static ActPlayerIdentity? ResolveIdentityById(
        IReadOnlyList<ActPlayerIdentity> identities,
        string actorId)
        => uint.TryParse(
                actorId,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var parsed)
            ? identities.FirstOrDefault(identity => identity.EntityId == parsed)
            : null;

    private static ParityDamageKind ResolveDamageKind(MasterSwing swing)
    {
        if (string.Equals(swing.Attacker, ChineseCombatChatContext.LimitBreakActorName,
                StringComparison.OrdinalIgnoreCase))
        {
            return ParityDamageKind.LimitBreak;
        }
        if (swing.SwingType == 2)
        {
            return ParityDamageKind.DamageOverTime;
        }
        if (swing.SwingType == 11)
        {
            return ParityDamageKind.DamageShield;
        }
        if (swing.SwingType == 1)
        {
            return string.Equals(swing.AttackType, "Attack", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(swing.AttackType, "攻击", StringComparison.Ordinal)
                ? ParityDamageKind.AutoAttack
                : ParityDamageKind.Direct;
        }
        return ParityDamageKind.Unknown;
    }

    private static string NormalizeActorId(string? actorId)
    {
        if (!uint.TryParse(
                actorId,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var parsed) || parsed == 0)
        {
            return string.Empty;
        }
        return FormatActorId(parsed);
    }

    private static string FormatActorId(uint actorId)
        => actorId == 0 ? string.Empty : actorId.ToString("X8", CultureInfo.InvariantCulture);

    private static string ResolveSha256(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return "unavailable";
            }
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            // Diagnostic hashing must never interfere with parser shutdown.
            return "unavailable";
        }
    }
}
