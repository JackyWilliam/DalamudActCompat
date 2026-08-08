using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DalamudActCompat.Meter;

internal sealed class ZoneNameLocalizer(
    IDataManager dataManager,
    IPluginLog log)
{
    private IReadOnlyDictionary<uint, string>? localizedTerritories;
    private IReadOnlyDictionary<string, string>? localizedNames;

    public string Localize(uint? territoryId, string zoneName)
    {
        try
        {
            if (localizedTerritories is null || localizedNames is null)
            {
                (localizedTerritories, localizedNames) = BuildLocalizedNames();
            }

            return ResolveByTerritory(
                territoryId,
                zoneName,
                localizedTerritories,
                localizedNames);
        }
        catch (Exception ex)
        {
            log.Warning($"Meter zone-name localization was unavailable: {ex.Message}");
            localizedTerritories = new Dictionary<uint, string>();
            localizedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return zoneName;
        }
    }

    internal static string Resolve(
        string zoneName,
        IReadOnlyDictionary<string, string> localizedNames)
        => localizedNames.TryGetValue(zoneName, out var localized) &&
           !string.IsNullOrWhiteSpace(localized)
            ? localized
            : zoneName;

    internal static string ResolveByTerritory(
        uint? territoryId,
        string zoneName,
        IReadOnlyDictionary<uint, string> localizedTerritories,
        IReadOnlyDictionary<string, string> localizedNames)
    {
        if (territoryId is > 0 &&
            localizedTerritories.TryGetValue(territoryId.Value, out var localized) &&
            !string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        return Resolve(zoneName, localizedNames);
    }

    private (
        IReadOnlyDictionary<uint, string> Territories,
        IReadOnlyDictionary<string, string> Names) BuildLocalizedNames()
    {
        var territories = new Dictionary<uint, string>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localizedSheet = dataManager.GetExcelSheet<TerritoryType>();
        var englishSheet = dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English);
        foreach (var localizedTerritory in localizedSheet)
        {
            var localized = localizedTerritory.PlaceName.ValueNullable?.Name.ToString();
            if (string.IsNullOrWhiteSpace(localized))
            {
                continue;
            }

            territories.TryAdd(localizedTerritory.RowId, localized);
            var englishTerritory = englishSheet.GetRow(localizedTerritory.RowId);
            var english = englishTerritory.PlaceName.ValueNullable?.Name.ToString();
            if (!string.IsNullOrWhiteSpace(english))
            {
                names.TryAdd(english, localized);
            }
        }

        return (territories, names);
    }
}
