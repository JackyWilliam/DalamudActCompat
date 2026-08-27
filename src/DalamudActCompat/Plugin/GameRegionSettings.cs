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
    string ClientLanguageName,
    byte? NativeClientLanguageCode)
{
    public bool IsManualOverride => Mode != GameRegionMode.Auto;

    public bool HasDetectedRegion => NativeClientLanguageCode is >= 0 and <= 4;

    public HostGameContext ToHostContext() => new(EffectiveRegion, ClientLanguage);
}

public static class GameRegionResolver
{
    public static GameRegionSelection Resolve(
        GameRegionMode mode,
        string? clientLanguageName,
        byte? nativeClientLanguageCode)
    {
        var normalizedLanguage = clientLanguageName?.Trim() ?? string.Empty;
        // Framework.ClientLanguage is the game's native client code. Unlike Dalamud's
        // data language, an international-client translation pack does not turn it into CN.
        var detectedRegion = nativeClientLanguageCode == 4
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
            string.IsNullOrWhiteSpace(normalizedLanguage) ? "Unknown" : normalizedLanguage,
            nativeClientLanguageCode);
    }

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
