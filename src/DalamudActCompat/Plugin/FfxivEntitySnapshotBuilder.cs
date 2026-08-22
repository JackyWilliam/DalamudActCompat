using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Plugin;

internal static class FfxivEntitySnapshotBuilder
{
    private const uint InvalidEntityId = 0xE0000000;

    public static HostFfxivEntitySnapshot Build(
        IObjectTable objectTable,
        IPartyList partyList,
        IClientState clientState,
        IPlayerState playerState,
        DateTimeOffset timestamp)
    {
        var partyTypes = BuildPartyTypes(partyList);
        var currentPlayerId = NormalizeEntityId(
            objectTable.LocalPlayer?.EntityId ?? playerState.EntityId);
        var combatantsById = new Dictionary<uint, HostFfxivCombatant>();
        foreach (var gameObject in objectTable)
        {
            if (!IsValidEntityId(gameObject.EntityId))
            {
                continue;
            }

            var combatant = MapCombatant(gameObject, partyTypes);
            if (!string.IsNullOrWhiteSpace(combatant.Name))
            {
                // Dalamud can expose two object-table slots with the same Entity ID during an
                // actor transition. The wire repository requires one current value per actor.
                combatantsById[combatant.Id] = combatant;
            }
        }

        return new HostFfxivEntitySnapshot(
            clientState.TerritoryType,
            currentPlayerId,
            timestamp,
            combatantsById.Values.ToArray());
    }

    internal static HostFfxivEntityDelta BuildDelta(
        HostFfxivEntitySnapshot baseline,
        HostFfxivEntitySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        if (baseline.TerritoryId != current.TerritoryId ||
            baseline.CurrentPlayerId != current.CurrentPlayerId)
        {
            throw new InvalidOperationException(
                "Entity deltas cannot cross territory or local-player snapshot boundaries.");
        }

        var baselineById = BuildCombatantMap(baseline.Combatants);
        var currentById = BuildCombatantMap(current.Combatants);
        var upserts = currentById.Values
            .Where(combatant =>
                !baselineById.TryGetValue(combatant.Id, out var previous) ||
                !EquivalentForIncrementalUpdate(previous, combatant))
            .ToArray();
        var removedIds = baselineById.Keys
            .Where(id => !currentById.ContainsKey(id))
            .ToArray();
        return new HostFfxivEntityDelta(
            current.TerritoryId,
            current.CurrentPlayerId,
            baseline.Timestamp,
            current.Timestamp,
            upserts,
            removedIds);
    }

    private static IReadOnlyDictionary<uint, int> BuildPartyTypes(IPartyList partyList)
    {
        var result = new Dictionary<uint, int>();
        var index = 0;
        foreach (var member in partyList)
        {
            if (IsValidEntityId(member.EntityId))
            {
                result[member.EntityId] = index < 8 ? 1 : 2;
            }

            index++;
        }

        return result;
    }

    private static HostFfxivCombatant MapCombatant(
        IGameObject gameObject,
        IReadOnlyDictionary<uint, int> partyTypes)
    {
        var character = gameObject as ICharacter;
        var battleChara = gameObject as IBattleChara;
        var player = gameObject as IPlayerCharacter;
        var statuses = battleChara?.StatusList
            .Where(status => status.StatusId is > 0 and <= ushort.MaxValue)
            .Select(status => new HostFfxivStatus(
                unchecked((ushort)status.StatusId),
                status.Param,
                status.RemainingTime,
                status.SourceId))
            .ToArray() ?? [];
        return new HostFfxivCombatant(
            gameObject.EntityId,
            gameObject.OwnerId,
            unchecked((byte)gameObject.ObjectKind),
            unchecked((int)(character?.ClassJob.RowId ?? 0)),
            character?.Level ?? 0,
            gameObject.Name.TextValue,
            character?.CurrentHp ?? 0,
            character?.MaxHp ?? 0,
            character?.CurrentMp ?? 0,
            character?.MaxMp ?? 0,
            character?.CurrentCp ?? 0,
            character?.MaxCp ?? 0,
            character?.CurrentGp ?? 0,
            character?.MaxGp ?? 0,
            battleChara?.IsCasting ?? false,
            battleChara?.CastActionId ?? 0,
            ToEntityId(battleChara?.CastTargetObjectId ?? 0),
            battleChara?.CurrentCastTime ?? 0,
            battleChara?.TotalCastTime ?? 0,
            gameObject.Position.X,
            gameObject.Position.Y,
            gameObject.Position.Z,
            gameObject.Rotation,
            player?.CurrentWorld.RowId ?? 0,
            player?.HomeWorld.RowId ?? 0,
            player?.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty,
            character?.NameId ?? 0,
            gameObject.BaseId,
            ToEntityId(gameObject.TargetObjectId),
            gameObject.CurrentDistance,
            partyTypes.GetValueOrDefault(gameObject.EntityId),
            gameObject.Address.ToInt64(),
            statuses);
    }

    private static bool EquivalentForIncrementalUpdate(
        HostFfxivCombatant left,
        HostFfxivCombatant right)
    {
        if (left.Id != right.Id ||
            left.OwnerId != right.OwnerId ||
            left.Type != right.Type ||
            left.Job != right.Job ||
            left.Level != right.Level ||
            left.Name != right.Name ||
            left.CurrentHp != right.CurrentHp ||
            left.MaxHp != right.MaxHp ||
            left.CurrentMp != right.CurrentMp ||
            left.MaxMp != right.MaxMp ||
            left.CurrentCp != right.CurrentCp ||
            left.MaxCp != right.MaxCp ||
            left.CurrentGp != right.CurrentGp ||
            left.MaxGp != right.MaxGp ||
            left.IsCasting != right.IsCasting ||
            left.CastId != right.CastId ||
            left.CastTargetId != right.CastTargetId ||
            left.MaxCastTime != right.MaxCastTime ||
            left.PosX != right.PosX ||
            left.PosY != right.PosY ||
            left.PosZ != right.PosZ ||
            left.Heading != right.Heading ||
            left.CurrentWorldId != right.CurrentWorldId ||
            left.WorldId != right.WorldId ||
            left.WorldName != right.WorldName ||
            left.BNpcNameId != right.BNpcNameId ||
            left.BNpcId != right.BNpcId ||
            left.TargetId != right.TargetId ||
            left.EffectiveDistance != right.EffectiveDistance ||
            left.PartyType != right.PartyType ||
            left.Address != right.Address ||
            left.Statuses.Count != right.Statuses.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Statuses.Count; index++)
        {
            var leftStatus = left.Statuses[index];
            var rightStatus = right.Statuses[index];
            // Cast progress and status remaining time tick continuously. The 500 ms full
            // snapshot refreshes them; excluding only those clocks keeps frame-rate deltas
            // bounded while additions, removals, positions, targets and effect changes stay live.
            if (leftStatus.Id != rightStatus.Id ||
                leftStatus.Param != rightStatus.Param ||
                leftStatus.SourceId != rightStatus.SourceId)
            {
                return false;
            }
        }

        return true;
    }

    private static uint ToEntityId(ulong objectId) => unchecked((uint)objectId);

    private static Dictionary<uint, HostFfxivCombatant> BuildCombatantMap(
        IEnumerable<HostFfxivCombatant> combatants)
    {
        var result = new Dictionary<uint, HostFfxivCombatant>();
        foreach (var combatant in combatants)
        {
            if (IsValidEntityId(combatant.Id))
            {
                // Protocol input is normalized defensively as focused tests and future senders
                // must not be able to turn a transient duplicate actor into a frame-loop fault.
                result[combatant.Id] = combatant;
            }
        }

        return result;
    }

    private static uint NormalizeEntityId(uint entityId)
        => IsValidEntityId(entityId) ? entityId : 0;

    private static bool IsValidEntityId(uint entityId)
        => entityId is not 0 and not InvalidEntityId;
}
