using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed class FriendDutySnapshotProvider
{
    private readonly Dictionary<uint, (uint Territory, CloudDutyActivity Duty)> duties = new();

    public FriendDutySnapshotProvider(IDataManager data, IPluginLog log)
    {
        try
        {
            foreach (var row in data.GetExcelSheet<ContentFinderCondition>())
            {
                var name = row.Name.ToString();
                if (row.TerritoryType.RowId > 0 && row.RowId is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(name))
                    duties.TryAdd(row.RowId, (row.TerritoryType.RowId, new(row.RowId, name)));
            }
        }
        catch (Exception error) { duties.Clear(); log.Warning(error, "Friend duty names unavailable; activity sharing stays empty."); }
    }

    public unsafe CloudDutyActivity? Read(IClientState client, ICondition condition)
    {
        var game = GameMain.Instance();
        return Resolve(client.IsLoggedIn, condition[ConditionFlag.BoundByDuty],
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51], client.TerritoryType,
            game == null ? 0u : game->CurrentContentFinderConditionId, duties);
    }

    // Only a confirmed, locally named duty is shareable. No field/zone fallback,
    // saved itinerary or battle-log inference is used during loading or logout.
    internal static CloudDutyActivity? Resolve(bool loggedIn, bool inDuty, bool loading, uint territory, uint conditionId,
        IReadOnlyDictionary<uint, (uint Territory, CloudDutyActivity Duty)> duties)
        // Several duty variants can share a territory. The game's actual content
        // id and territory must agree; selecting the first sheet row would guess.
        => loggedIn && inDuty && !loading && duties.TryGetValue(conditionId, out var match) && match.Territory == territory ? match.Duty : null;
}
