using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Plugin;

internal static class FfxivEntitySnapshotBuilder
{
    public static HostFfxivEntitySnapshot Build(
        IObjectTable objectTable,
        IPartyList partyList,
        IClientState clientState,
        IPlayerState playerState,
        DateTimeOffset timestamp)
    {
        var partyTypes = BuildPartyTypes(partyList);
        var currentPlayerId = objectTable.LocalPlayer?.EntityId ?? playerState.EntityId;
        var combatants = objectTable
            .Where(gameObject => gameObject.EntityId != 0)
            .Select(gameObject => MapCombatant(gameObject, partyTypes))
            .Where(combatant => !string.IsNullOrWhiteSpace(combatant.Name))
            .ToArray();
        return new HostFfxivEntitySnapshot(
            clientState.TerritoryType,
            currentPlayerId,
            timestamp,
            combatants);
    }

    private static IReadOnlyDictionary<uint, int> BuildPartyTypes(IPartyList partyList)
    {
        var result = new Dictionary<uint, int>();
        var index = 0;
        foreach (var member in partyList)
        {
            if (member.EntityId != 0)
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

    private static uint ToEntityId(ulong objectId) => unchecked((uint)objectId);
}
