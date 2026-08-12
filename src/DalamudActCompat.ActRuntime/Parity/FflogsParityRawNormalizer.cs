using System.Globalization;

namespace DalamudActCompat.ActRuntime.Parity;

/// <summary>
/// Converts the public FFXIV_ACT_Plugin pipe format into the canonical parity
/// event shape. Runtime diagnostics still use ACT's MasterSwing output as the
/// authoritative normalized layer; this decoder exists to make raw fixtures
/// reproducible and to expose packet-level evidence alongside that output.
/// </summary>
internal sealed class FflogsParityRawNormalizer
{
    private static readonly HashSet<byte> DamageEffectTypes = [1, 3, 5, 6];
    private readonly HashSet<string> partyActorIds;
    private readonly Dictionary<string, ActorState> actors =
        new(StringComparer.OrdinalIgnoreCase);
    private long sequence;

    public FflogsParityRawNormalizer(IEnumerable<string> partyActorIds)
    {
        ArgumentNullException.ThrowIfNull(partyActorIds);
        this.partyActorIds = partyActorIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(NormalizeActorId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, (string Name, string OwnerId)> Actors
        => actors.ToDictionary(
            static pair => pair.Key,
            static pair => (pair.Value.Name, pair.Value.OwnerId),
            StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ParityReplayEvent> Normalize(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return [];
        }

        var fields = rawLine.TrimEnd('\r', '\n').Split('|');
        if (fields.Length < 2 || !DateTimeOffset.TryParse(
                fields[1],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return [];
        }

        return fields[0] switch
        {
            "03" => NormalizeActor(fields, timestamp),
            "21" or "22" => NormalizeAbility(fields, timestamp),
            "24" => NormalizePeriodicEffect(fields, timestamp),
            "25" => NormalizeDeath(fields, timestamp),
            "34" => NormalizeTargetability(fields, timestamp),
            _ => [],
        };
    }

    public IReadOnlyList<ParityReplayEvent> Normalize(IEnumerable<string> rawLines)
    {
        ArgumentNullException.ThrowIfNull(rawLines);
        var result = new List<ParityReplayEvent>();
        foreach (var rawLine in rawLines)
        {
            result.AddRange(Normalize(rawLine));
        }
        return result;
    }

    private IReadOnlyList<ParityReplayEvent> NormalizeActor(
        IReadOnlyList<string> fields,
        DateTimeOffset timestamp)
    {
        if (fields.Count < 7 || string.IsNullOrWhiteSpace(fields[2]))
        {
            return [];
        }

        var actorId = NormalizeActorId(fields[2]);
        var ownerId = IsActorId(fields[6]) ? NormalizeActorId(fields[6]) : string.Empty;
        actors[actorId] = new ActorState(fields[3], ownerId);
        return
        [
            new ParityReplayEvent
            {
                Kind = ParityReplayEventKind.Actor,
                Timestamp = timestamp,
                Sequence = NextSequence(),
                SourceId = actorId,
                SourceName = fields[3],
                OwnerId = ownerId,
                IsPartyMember = partyActorIds.Contains(actorId),
                RawLineType = fields[0],
                Evidence = "FFXIV_ACT_Plugin AddCombatant pipe line",
            },
        ];
    }

    private IReadOnlyList<ParityReplayEvent> NormalizeAbility(
        IReadOnlyList<string> fields,
        DateTimeOffset timestamp)
    {
        if (fields.Count < 46)
        {
            return [];
        }

        var sourceId = NormalizeActorId(fields[2]);
        var targetId = NormalizeActorId(fields[6]);
        var ownerId = ResolveOwnerId(sourceId);
        var result = new List<ParityReplayEvent>(2);
        for (var effectIndex = 8; effectIndex < 24; effectIndex += 2)
        {
            if (!TryDecodeDamageEffect(
                    fields[effectIndex],
                    fields[effectIndex + 1],
                    out var amount,
                    out var critical,
                    out var directHit))
            {
                continue;
            }

            result.Add(new ParityReplayEvent
            {
                Kind = ParityReplayEventKind.Damage,
                Timestamp = timestamp,
                Sequence = NextSequence(),
                SourceId = sourceId,
                SourceName = ResolveActorName(sourceId, fields[3]),
                OwnerId = ownerId,
                TargetId = targetId,
                TargetName = ResolveActorName(targetId, fields[7]),
                AbilityId = fields[4],
                AbilityName = fields[5],
                Amount = amount,
                TargetCurrentHp = ParseDecimalLong(fields[24]),
                Critical = critical,
                DirectHit = directHit,
                IsPartyMember = IsPartyOwned(sourceId, ownerId),
                IsDamageSwing = true,
                DamageKind = IsAutoAttack(fields[5])
                    ? ParityDamageKind.AutoAttack
                    : ParityDamageKind.Direct,
                RawLineType = fields[0],
                PacketId = fields[44],
                Evidence = $"ActionEffect slot {(effectIndex - 8) / 2}",
            });
        }
        return result;
    }

    private IReadOnlyList<ParityReplayEvent> NormalizePeriodicEffect(
        IReadOnlyList<string> fields,
        DateTimeOffset timestamp)
    {
        if (fields.Count < 20 || !string.Equals(fields[4], "DoT", StringComparison.Ordinal))
        {
            return [];
        }

        var sourceId = NormalizeActorId(fields[17]);
        var targetId = NormalizeActorId(fields[2]);
        var ownerId = ResolveOwnerId(sourceId);
        if (!long.TryParse(fields[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var amount))
        {
            return [];
        }

        return
        [
            new ParityReplayEvent
            {
                Kind = ParityReplayEventKind.Damage,
                Timestamp = timestamp,
                Sequence = NextSequence(),
                SourceId = sourceId,
                SourceName = ResolveActorName(sourceId, fields[18]),
                OwnerId = ownerId,
                TargetId = targetId,
                TargetName = ResolveActorName(targetId, fields[3]),
                AbilityId = fields[5],
                AbilityName = "DoT",
                Amount = amount,
                TargetCurrentHp = ParseDecimalLong(fields[7]),
                IsPartyMember = IsPartyOwned(sourceId, ownerId),
                IsDamageSwing = true,
                DamageKind = ParityDamageKind.DamageOverTime,
                RawLineType = fields[0],
                Evidence = "Network DoT tick; crit/direct flags require parser simulation and remain unknown here",
            },
        ];
    }

    private IReadOnlyList<ParityReplayEvent> NormalizeDeath(
        IReadOnlyList<string> fields,
        DateTimeOffset timestamp)
    {
        if (fields.Count < 6)
        {
            return [];
        }

        return
        [
            new ParityReplayEvent
            {
                Kind = ParityReplayEventKind.Death,
                Timestamp = timestamp,
                Sequence = NextSequence(),
                SourceId = NormalizeActorId(fields[4]),
                SourceName = fields[5],
                TargetId = NormalizeActorId(fields[2]),
                TargetName = fields[3],
                RawLineType = fields[0],
                Evidence = "Network Death pipe line",
            },
        ];
    }

    private IReadOnlyList<ParityReplayEvent> NormalizeTargetability(
        IReadOnlyList<string> fields,
        DateTimeOffset timestamp)
    {
        if (fields.Count < 7 || (fields[6] != "00" && fields[6] != "01"))
        {
            return [];
        }

        return
        [
            new ParityReplayEvent
            {
                Kind = ParityReplayEventKind.Targetability,
                Timestamp = timestamp,
                Sequence = NextSequence(),
                TargetId = NormalizeActorId(fields[2]),
                TargetName = fields[3],
                Targetable = fields[6] == "01",
                RawLineType = fields[0],
                Evidence = $"NameToggle state {fields[6]}",
            },
        ];
    }

    internal static bool TryDecodeDamageEffect(
        string flagsText,
        string valueText,
        out long amount,
        out bool critical,
        out bool directHit)
    {
        amount = 0;
        critical = false;
        directHit = false;
        var flags = flagsText.PadLeft(8, '0');
        var value = valueText.PadLeft(8, '0');
        if (flags.Length != 8 || value.Length != 8 ||
            !byte.TryParse(flags.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var type) ||
            !DamageEffectTypes.Contains(type) ||
            !byte.TryParse(flags.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var param0) ||
            !ushort.TryParse(value.AsSpan(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var baseAmount) ||
            !byte.TryParse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags2) ||
            !byte.TryParse(value.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags1))
        {
            return false;
        }

        // FFXIV_ACT_Plugin stores large values as a 16-bit base plus Flags1 * 65536
        // when Flags2 bit 0x40 is set. Matching this public pipe representation is
        // required for raw fixtures to remain comparable with the compiled parser.
        amount = baseAmount + ((flags2 & 0x40) != 0 ? flags1 * 65_536L : 0);
        critical = (param0 & 0x20) != 0;
        directHit = (param0 & 0x40) != 0;
        return true;
    }

    private bool IsPartyOwned(string sourceId, string ownerId)
        => partyActorIds.Contains(sourceId) ||
           (!string.IsNullOrWhiteSpace(ownerId) && partyActorIds.Contains(ownerId));

    private string ResolveOwnerId(string actorId)
        => actors.TryGetValue(actorId, out var actor) ? actor.OwnerId : string.Empty;

    private string ResolveActorName(string actorId, string fallback)
        => actors.TryGetValue(actorId, out var actor) && !string.IsNullOrWhiteSpace(actor.Name)
            ? actor.Name
            : fallback;

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

    private static string NormalizeActorId(string value)
        => value.Trim().ToUpperInvariant();

    private static bool IsAutoAttack(string actionName)
        => string.Equals(actionName, "Attack", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(actionName, "攻击", StringComparison.Ordinal);

    private long NextSequence() => ++sequence;

    private sealed record ActorState(string Name, string OwnerId);
}
