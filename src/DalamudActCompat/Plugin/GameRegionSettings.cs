using DalamudActCompat.Protocol;

namespace DalamudActCompat.Plugin;

public enum GameRegionMode
{
    Auto,
    Chinese,
    Global,
}

public sealed record GameRegionSelection(
    GameRegionMode Mode,
    HostGameRegion DetectedRegion,
    HostGameRegion EffectiveRegion,
    HostClientLanguage ClientLanguage,
    string ClientLanguageName)
{
    public bool IsManualOverride => Mode != GameRegionMode.Auto;

    public HostGameContext ToHostContext() => new(EffectiveRegion, ClientLanguage);
}

public static class GameRegionResolver
{
    public static GameRegionSelection Resolve(GameRegionMode mode, string? clientLanguageName)
    {
        var normalizedLanguage = clientLanguageName?.Trim() ?? string.Empty;
        // XIVLauncherCN exposes ChineseSimplified; every official international
        // client language uses the Global packet/opcode family.
        var detectedRegion = IsChineseClientLanguage(normalizedLanguage)
            ? HostGameRegion.Chinese
            : HostGameRegion.Global;
        var effectiveRegion = mode switch
        {
            GameRegionMode.Chinese => HostGameRegion.Chinese,
            GameRegionMode.Global => HostGameRegion.Global,
            _ => detectedRegion,
        };

        return new GameRegionSelection(
            mode,
            detectedRegion,
            effectiveRegion,
            ResolveClientLanguage(normalizedLanguage),
            string.IsNullOrWhiteSpace(normalizedLanguage) ? "Unknown" : normalizedLanguage);
    }

    private static bool IsChineseClientLanguage(string languageName)
        => languageName is "ChineseSimplified" or "Chinese";

    private static HostClientLanguage ResolveClientLanguage(string languageName)
        => languageName switch
        {
            "Japanese" => HostClientLanguage.Japanese,
            "German" => HostClientLanguage.German,
            "French" => HostClientLanguage.French,
            "ChineseSimplified" or "Chinese" => HostClientLanguage.Chinese,
            "Korean" => HostClientLanguage.Korean,
            _ => HostClientLanguage.English,
        };
}
