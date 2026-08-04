using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DalamudActCompat.Meter;

internal sealed class ZoneNameLocalizer(
    IDataManager dataManager,
    IPluginLog log)
{
    private IReadOnlyDictionary<string, string>? localizedNames;

    public string Localize(string zoneName)
    {
        if (string.IsNullOrWhiteSpace(zoneName))
        {
            return zoneName;
        }

        try
        {
            localizedNames ??= BuildLocalizedNames();
            return Resolve(zoneName, localizedNames);
        }
        catch (Exception ex)
        {
            log.Warning($"Meter zone-name localization was unavailable: {ex.Message}");
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

    private IReadOnlyDictionary<string, string> BuildLocalizedNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localizedSheet = dataManager.GetExcelSheet<TerritoryType>();
        var englishSheet = dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English);
        foreach (var localizedTerritory in localizedSheet)
        {
            var localized = localizedTerritory.PlaceName.ValueNullable?.Name.ToString();
            if (string.IsNullOrWhiteSpace(localized))
            {
                continue;
            }

            var englishTerritory = englishSheet.GetRow(localizedTerritory.RowId);
            var english = englishTerritory.PlaceName.ValueNullable?.Name.ToString();
            if (!string.IsNullOrWhiteSpace(english))
            {
                map.TryAdd(english, localized);
            }
        }

        return map;
    }
}
