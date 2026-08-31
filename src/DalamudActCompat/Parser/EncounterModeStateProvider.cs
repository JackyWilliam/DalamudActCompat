using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;
using DynamicEventRow = Lumina.Excel.Sheets.DynamicEvent;

namespace DalamudActCompat.Parser;

internal sealed class EncounterModeStateProvider
{
    private static readonly IReadOnlySet<uint> ExplorationIntendedUses =
        new HashSet<uint> { 26, 38, 41, 47, 48, 61 };

    private readonly object snapshotLock = new();
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly Func<bool> isDutyPartyWiped;
    private readonly IPluginLog log;
    private readonly IReadOnlySet<uint> knownTerritories;
    private readonly IReadOnlySet<uint> explorationTerritories;
    private readonly IReadOnlyDictionary<uint, IReadOnlySet<uint>> internalMapsByTerritory;
    private readonly IReadOnlySet<uint> baldesionArsenalMaps;
    private readonly IReadOnlySet<ushort> largeScaleDynamicEventIds;
    private EncounterModeSnapshot snapshot;
    private bool dynamicEventReadFaulted;

    public EncounterModeStateProvider(
        IDataManager dataManager,
        IClientState clientState,
        ICondition condition,
        Func<bool> isDutyPartyWiped,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.condition = condition;
        this.isDutyPartyWiped = isDutyPartyWiped;
        this.log = log;

        try
        {
            var territoryRows = dataManager.GetExcelSheet<TerritoryType>().ToArray();
            var mapRows = dataManager.GetExcelSheet<Map>().ToArray();
            knownTerritories = territoryRows
                .Select(static row => row.RowId)
                .ToHashSet();
            explorationTerritories = territoryRows
                .Where(row => IsExplorationIntendedUse(row.TerritoryIntendedUse.RowId))
                .Select(static row => row.RowId)
                .ToHashSet();

            var mainPlaceNames = territoryRows.ToDictionary(
                static row => row.RowId,
                static row => row.Map.ValueNullable?.PlaceName.RowId ?? 0u);
            internalMapsByTerritory = mapRows
                .Where(row =>
                    mainPlaceNames.TryGetValue(row.TerritoryType.RowId, out var mainPlaceName) &&
                    mainPlaceName != 0 &&
                    row.PlaceName.RowId != mainPlaceName)
                .GroupBy(static row => row.TerritoryType.RowId)
                .ToDictionary(
                    static group => group.Key,
                    static group => (IReadOnlySet<uint>)group
                        .Select(static row => row.RowId)
                        .ToHashSet());

            var baldesionTerritories = territoryRows
                .Where(static row => row.TerritoryIntendedUse.RowId == 41)
                .Select(static row => row.RowId)
                .ToHashSet();
            baldesionArsenalMaps = internalMapsByTerritory
                .Where(pair => baldesionTerritories.Contains(pair.Key))
                .SelectMany(static pair => pair.Value)
                .ToHashSet();
            largeScaleDynamicEventIds = dataManager.GetExcelSheet<DynamicEventRow>()
                .Where(static row => row.EventType.RowId == 4 && row.RowId <= ushort.MaxValue)
                .Select(static row => (ushort)row.RowId)
                .ToHashSet();
        }
        catch (Exception ex)
        {
            // Missing or changed sheets must fall back to BoundByDuty rather than preventing
            // the plugin from loading after a game update.
            knownTerritories = new HashSet<uint>();
            explorationTerritories = new HashSet<uint>();
            internalMapsByTerritory = new Dictionary<uint, IReadOnlySet<uint>>();
            baldesionArsenalMaps = new HashSet<uint>();
            largeScaleDynamicEventIds = new HashSet<ushort>();
            log.Warning(ex, "Encounter-mode game tables could not be loaded; using BoundByDuty fallback.");
        }

        Update();
    }

    public EncounterModeSnapshot Read()
    {
        lock (snapshotLock)
        {
            return snapshot;
        }
    }

    internal static bool IsExplorationIntendedUse(uint intendedUse)
        => ExplorationIntendedUses.Contains(intendedUse);

    public void Update()
    {
        var previous = Read();
        var loading = condition[ConditionFlag.BetweenAreas] ||
                      condition[ConditionFlag.BetweenAreas51];
        var territoryId = clientState.TerritoryType;
        var mapId = clientState.MapId;
        var territoryKnown = knownTerritories.Contains(territoryId);
        var explorationTerritory = explorationTerritories.Contains(territoryId);
        var internalMap = internalMapsByTerritory.TryGetValue(
                              territoryId,
                              out var internalMaps) &&
                          internalMaps.Contains(mapId);
        var largeScaleDynamicEventInside = explorationTerritory &&
                                           internalMap &&
                                           TryReadLargeScaleDynamicEvent();
        var baldesionArsenalInside = explorationTerritory &&
                                     baldesionArsenalMaps.Contains(mapId);
        var mode = EncounterModePolicy.Resolve(
            previous.Mode,
            loading,
            territoryKnown,
            explorationTerritory,
            condition[ConditionFlag.BoundByDuty],
            largeScaleDynamicEventInside,
            baldesionArsenalInside);

        if (loading && previous.Mode == EncounterMode.LargeScaleFieldDuty)
        {
            // Territory and map can briefly become zero while changing floors. Retaining the
            // last confirmed primitives also keeps the final history record attributed correctly.
            territoryId = previous.TerritoryId;
            mapId = previous.MapId;
        }

        var next = new EncounterModeSnapshot(
            territoryId,
            mapId,
            mode,
            condition[ConditionFlag.InCombat],
            mode == EncounterMode.DutyAttempt && isDutyPartyWiped(),
            loading);
        lock (snapshotLock)
        {
            snapshot = next;
        }
    }

    private unsafe bool TryReadLargeScaleDynamicEvent()
    {
        try
        {
            var container = DynamicEventContainer.GetInstance();
            var currentEvent = container is null ? null : container->GetCurrentEvent();
            var active = currentEvent is not null &&
                         currentEvent->State != DynamicEventState.Inactive &&
                         largeScaleDynamicEventIds.Contains(currentEvent->DynamicEventId);
            dynamicEventReadFaulted = false;
            return active;
        }
        catch (Exception ex)
        {
            if (!dynamicEventReadFaulted)
            {
                dynamicEventReadFaulted = true;
                log.Warning(ex, "Current dynamic event could not be read; treating the frame as ordinary exploration content.");
            }
            return false;
        }
    }
}
