using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Statuses;
using Dalamud.Plugin.Services;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Plugin;

internal static class FfxivEntitySnapshotBuilder
{
    private const uint InvalidEntityId = 0xE0000000;
    private static readonly ConcurrentDictionary<uint, string> WorldNameCache = new();

    public static HostFfxivEntitySnapshot Build(
        IObjectTable objectTable,
        IPartyList partyList,
        IClientState clientState,
        IPlayerState playerState,
        DateTimeOffset timestamp)
        => Build(objectTable, partyList, clientState, playerState, timestamp, out _);

    public static HostFfxivEntitySnapshot Build(
        IObjectTable objectTable,
        IPartyList partyList,
        IClientState clientState,
        IPlayerState playerState,
        DateTimeOffset timestamp,
        out Dictionary<uint, HostFfxivCombatant> combatantsById)
    {
        var partyTypes = BuildPartyTypes(partyList);
        var currentPlayerId = NormalizeEntityId(
            objectTable.LocalPlayer?.EntityId ?? playerState.EntityId);
        combatantsById = new Dictionary<uint, HostFfxivCombatant>(Math.Max(16, objectTable.Length));
        foreach (var gameObject in objectTable)
        {
            if (!IsValidEntityId(gameObject.EntityId))
            {
                continue;
            }

            var combatant = MapCombatant(gameObject, partyTypes);
            // Scene objects may have no display name. Utils resolves their BaseId and position
            // by Entity ID for U7b phase changes and half-room attacks, so keep them in the Host.
            // Duplicate object-table slots during actor transitions still collapse to one value.
            combatantsById[combatant.Id] = combatant;
        }

        var combatants = new HostFfxivCombatant[combatantsById.Count];
        combatantsById.Values.CopyTo(combatants, 0);
        return new HostFfxivEntitySnapshot(
            clientState.TerritoryType,
            currentPlayerId,
            timestamp,
            combatants);
    }

    internal static HostFfxivEntityDelta? TryBuildDeltaDirect(
        IObjectTable objectTable,
        IPartyList partyList,
        IClientState clientState,
        IPlayerState playerState,
        DateTimeOffset timestamp,
        HostFfxivEntitySnapshot baseline,
        Dictionary<uint, HostFfxivCombatant> baselineById,
        long epoch,
        Dictionary<uint, int> partyTypesBuffer,
        HashSet<uint> seenIdsBuffer)
    {
        var territoryId = clientState.TerritoryType;
        var currentPlayerId = NormalizeEntityId(
            objectTable.LocalPlayer?.EntityId ?? playerState.EntityId);
        if (baseline.TerritoryId != territoryId ||
            baseline.CurrentPlayerId != currentPlayerId)
        {
            return null;
        }

        FillPartyTypes(partyList, partyTypesBuffer);
        seenIdsBuffer.Clear();
        Dictionary<uint, HostFfxivCombatant>? upsertsById = null;
        HashSet<uint>? keepStatusIds = null;

        foreach (var gameObject in objectTable)
        {
            var entityId = gameObject.EntityId;
            if (!IsValidEntityId(entityId))
            {
                continue;
            }

            seenIdsBuffer.Add(entityId);
            if (!baselineById.TryGetValue(entityId, out var previous))
            {
                (upsertsById ??= new Dictionary<uint, HostFfxivCombatant>())[entityId] =
                    MapCombatant(gameObject, partyTypesBuffer);
                keepStatusIds?.Remove(entityId);
                continue;
            }

            var character = gameObject as ICharacter;
            var battleChara = gameObject as IBattleChara;
            var statusList = battleChara?.StatusList;
            var sameStatuses = MatchesLiveStatuses(previous.Statuses, statusList);
            if (sameStatuses && MatchesLiveFields(previous, gameObject, character, battleChara, partyTypesBuffer))
            {
                // If an earlier duplicate slot for this same EntityId differed, overwrite with clean state.
                if (upsertsById is not null && upsertsById.Remove(entityId))
                {
                    keepStatusIds?.Remove(entityId);
                }
                continue;
            }

            if (sameStatuses && previous.Statuses.Count != 0)
            {
                // Only the position, HP or cast changed. The Host already holds these statuses
                // from the baseline, and they are most of what an actor weighs on the wire.
                (upsertsById ??= new Dictionary<uint, HostFfxivCombatant>())[entityId] =
                    MapCombatantWithStatuses(gameObject, character, battleChara, partyTypesBuffer, Array.Empty<HostFfxivStatus>());
                (keepStatusIds ??= new HashSet<uint>()).Add(entityId);
            }
            else
            {
                (upsertsById ??= new Dictionary<uint, HostFfxivCombatant>())[entityId] =
                    MapCombatantWithStatuses(gameObject, character, battleChara, partyTypesBuffer, CopyStatuses(statusList));
                keepStatusIds?.Remove(entityId);
            }
        }

        List<uint>? removedIds = null;
        foreach (var id in baselineById.Keys)
        {
            if (!seenIdsBuffer.Contains(id))
            {
                (removedIds ??= new List<uint>()).Add(id);
            }
        }

        IReadOnlyList<HostFfxivCombatant> upserts = upsertsById is null || upsertsById.Count == 0
            ? Array.Empty<HostFfxivCombatant>()
            : CopyValues(upsertsById);
        IReadOnlyList<uint> removed = removedIds is null || removedIds.Count == 0
            ? Array.Empty<uint>()
            : removedIds;
        IReadOnlyList<uint>? keepStatuses = keepStatusIds is null || keepStatusIds.Count == 0
            ? null
            : CopyIds(keepStatusIds);

        return new HostFfxivEntityDelta(
            territoryId,
            currentPlayerId,
            baseline.Timestamp,
            timestamp,
            upserts,
            removed,
            Epoch: epoch,
            KeepStatusIds: keepStatuses);
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
        var upserts = new List<HostFfxivCombatant>();
        List<uint>? keepStatusIds = null;
        foreach (var combatant in currentById.Values)
        {
            if (!baselineById.TryGetValue(combatant.Id, out var previous))
            {
                upserts.Add(combatant);
                continue;
            }

            var sameStatuses = EquivalentStatuses(previous, combatant);
            if (sameStatuses && EquivalentFields(previous, combatant))
            {
                continue;
            }

            if (sameStatuses && combatant.Statuses.Count != 0)
            {
                // Only the position, HP or cast changed. The Host already holds these statuses
                // from the baseline, and they are most of what an actor weighs on the wire.
                upserts.Add(combatant with { Statuses = [] });
                (keepStatusIds ??= []).Add(combatant.Id);
            }
            else
            {
                upserts.Add(combatant);
            }
        }

        List<uint>? removedList = null;
        foreach (var id in baselineById.Keys)
        {
            if (!currentById.ContainsKey(id))
            {
                (removedList ??= []).Add(id);
            }
        }

        return new HostFfxivEntityDelta(
            current.TerritoryId,
            current.CurrentPlayerId,
            baseline.Timestamp,
            current.Timestamp,
            upserts,
            removedList is null ? Array.Empty<uint>() : removedList,
            KeepStatusIds: keepStatusIds);
    }

    private static Dictionary<uint, int> BuildPartyTypes(IPartyList partyList)
    {
        var result = new Dictionary<uint, int>(partyList.Length);
        FillPartyTypes(partyList, result);
        return result;
    }

    private static void FillPartyTypes(IPartyList partyList, Dictionary<uint, int> result)
    {
        result.Clear();
        var index = 0;
        foreach (var member in partyList)
        {
            if (IsValidEntityId(member.EntityId))
            {
                result[member.EntityId] = index < 8 ? 1 : 2;
            }

            index++;
        }
    }

    private static HostFfxivCombatant MapCombatant(
        IGameObject gameObject,
        IReadOnlyDictionary<uint, int> partyTypes)
    {
        var character = gameObject as ICharacter;
        var battleChara = gameObject as IBattleChara;
        return MapCombatantWithStatuses(
            gameObject,
            character,
            battleChara,
            partyTypes,
            CopyStatuses(battleChara?.StatusList));
    }

    private static HostFfxivCombatant MapCombatantWithStatuses(
        IGameObject gameObject,
        ICharacter? character,
        IBattleChara? battleChara,
        IReadOnlyDictionary<uint, int> partyTypes,
        HostFfxivStatus[] statuses)
    {
        var player = gameObject as IPlayerCharacter;
        var homeWorldId = player?.HomeWorld.RowId ?? 0;
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
            homeWorldId,
            ResolveHomeWorldName(player, homeWorldId),
            character?.NameId ?? 0,
            gameObject.BaseId,
            ToEntityId(gameObject.TargetObjectId),
            gameObject.CurrentDistance,
            partyTypes.GetValueOrDefault(gameObject.EntityId),
            gameObject.Address.ToInt64(),
            statuses);
    }

    private static string ResolveHomeWorldName(IPlayerCharacter? player, uint homeWorldId)
    {
        if (player is null || homeWorldId == 0)
        {
            return string.Empty;
        }

        if (WorldNameCache.TryGetValue(homeWorldId, out var cached))
        {
            return cached;
        }

        var resolved = player.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty;
        if (resolved.Length > 0)
        {
            WorldNameCache.TryAdd(homeWorldId, resolved);
        }

        return resolved;
    }

    private static HostFfxivStatus[] CopyStatuses(StatusList? statusList)
    {
        // Dalamud wraps GetStatusManager() without a null check. A battle object that is
        // spawning or despawning has none yet, and enumerating it would throw and discard
        // the whole frame's snapshot, so treat it as having no statuses.
        if (statusList is null || statusList.Address == 0)
        {
            return Array.Empty<HostFfxivStatus>();
        }

        List<HostFfxivStatus>? buffer = null;
        foreach (var status in statusList)
        {
            if (status.StatusId is > 0 and <= ushort.MaxValue)
            {
                (buffer ??= new List<HostFfxivStatus>(8)).Add(new HostFfxivStatus(
                    unchecked((ushort)status.StatusId),
                    status.Param,
                    status.RemainingTime,
                    status.SourceId));
            }
        }

        return buffer is null ? Array.Empty<HostFfxivStatus>() : buffer.ToArray();
    }

    private static bool MatchesLiveStatuses(
        IReadOnlyList<HostFfxivStatus> previous,
        StatusList? statusList)
    {
        if (statusList is null || statusList.Address == 0)
        {
            return previous.Count == 0;
        }

        var index = 0;
        var expectedCount = previous.Count;
        foreach (var status in statusList)
        {
            if (status.StatusId is not (> 0 and <= ushort.MaxValue))
            {
                continue;
            }

            if (index >= expectedCount)
            {
                return false;
            }

            var prev = previous[index++];
            if (prev.Id != unchecked((ushort)status.StatusId) ||
                prev.Param != status.Param ||
                prev.SourceId != status.SourceId)
            {
                return false;
            }
        }

        return index == expectedCount;
    }

    private static bool MatchesLiveFields(
        HostFfxivCombatant previous,
        IGameObject gameObject,
        ICharacter? character,
        IBattleChara? battleChara,
        IReadOnlyDictionary<uint, int> partyTypes)
    {
        var pos = gameObject.Position;
        if (previous.PosX != pos.X ||
            previous.PosY != pos.Y ||
            previous.PosZ != pos.Z ||
            previous.Heading != gameObject.Rotation ||
            previous.CurrentHp != (character?.CurrentHp ?? 0) ||
            previous.MaxHp != (character?.MaxHp ?? 0) ||
            previous.CurrentMp != (character?.CurrentMp ?? 0) ||
            previous.MaxMp != (character?.MaxMp ?? 0) ||
            previous.TargetId != ToEntityId(gameObject.TargetObjectId) ||
            previous.IsCasting != (battleChara?.IsCasting ?? false) ||
            previous.CastId != (battleChara?.CastActionId ?? 0) ||
            previous.CastTargetId != ToEntityId(battleChara?.CastTargetObjectId ?? 0) ||
            previous.MaxCastTime != (battleChara?.TotalCastTime ?? 0) ||
            previous.EffectiveDistance != gameObject.CurrentDistance ||
            previous.Address != gameObject.Address.ToInt64() ||
            previous.OwnerId != gameObject.OwnerId ||
            previous.Type != unchecked((byte)gameObject.ObjectKind) ||
            previous.Job != unchecked((int)(character?.ClassJob.RowId ?? 0)) ||
            previous.Level != (character?.Level ?? 0) ||
            previous.CurrentCp != (character?.CurrentCp ?? 0) ||
            previous.MaxCp != (character?.MaxCp ?? 0) ||
            previous.CurrentGp != (character?.CurrentGp ?? 0) ||
            previous.MaxGp != (character?.MaxGp ?? 0) ||
            previous.BNpcNameId != (character?.NameId ?? 0) ||
            previous.BNpcId != gameObject.BaseId ||
            previous.PartyType != partyTypes.GetValueOrDefault(gameObject.EntityId))
        {
            return false;
        }

        var player = gameObject as IPlayerCharacter;
        var currentWorldId = player?.CurrentWorld.RowId ?? 0;
        var homeWorldId = player?.HomeWorld.RowId ?? 0;
        if (previous.CurrentWorldId != currentWorldId ||
            previous.WorldId != homeWorldId)
        {
            return false;
        }

        // Only marshal SeString Name if the actor spawned with an empty name in the baseline.
        if (previous.Name.Length == 0 && !string.IsNullOrEmpty(gameObject.Name.TextValue))
        {
            return false;
        }

        return true;
    }

    private static HostFfxivCombatant[] CopyValues(Dictionary<uint, HostFfxivCombatant> map)
    {
        var result = new HostFfxivCombatant[map.Count];
        map.Values.CopyTo(result, 0);
        return result;
    }

    private static uint[] CopyIds(HashSet<uint> ids)
    {
        var result = new uint[ids.Count];
        ids.CopyTo(result);
        return result;
    }

    private static bool EquivalentFields(
        HostFfxivCombatant left,
        HostFfxivCombatant right)
        => !(left.Id != right.Id ||
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
            left.Address != right.Address);

    private static bool EquivalentStatuses(
        HostFfxivCombatant left,
        HostFfxivCombatant right)
    {
        if (left.Statuses.Count != right.Statuses.Count)
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
        IReadOnlyList<HostFfxivCombatant> combatants)
    {
        var result = new Dictionary<uint, HostFfxivCombatant>(combatants.Count);
        for (var index = 0; index < combatants.Count; index++)
        {
            var combatant = combatants[index];
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
