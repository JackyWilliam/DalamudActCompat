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
    string? DetectedLauncherName)
{
    public bool IsManualOverride => Mode != GameRegionMode.Auto;

    public bool HasDetectedLauncher => !string.IsNullOrWhiteSpace(DetectedLauncherName);

    public HostGameContext ToHostContext() => new(EffectiveRegion, ClientLanguage);
}

public static class GameRegionResolver
{
    private const string ChineseLauncherName = "XIVLauncherCN";
    private const string GlobalLauncherName = "XIVLauncher";

    public static GameRegionSelection Resolve(
        GameRegionMode mode,
        string? clientLanguageName,
        string? pluginConfigDirectory)
    {
        var normalizedLanguage = clientLanguageName?.Trim() ?? string.Empty;
        var detectedLauncherName = FindLauncherName(pluginConfigDirectory);
        // Language packs can expose ChineseSimplified on the international client.
        // The launcher-owned config root identifies the packet/opcode family instead.
        var detectedRegion = string.Equals(
                detectedLauncherName,
                ChineseLauncherName,
                StringComparison.OrdinalIgnoreCase)
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
            detectedLauncherName);
    }

    private static string? FindLauncherName(string? pluginConfigDirectory)
    {
        if (string.IsNullOrWhiteSpace(pluginConfigDirectory))
        {
            return null;
        }

        for (var current = new DirectoryInfo(Path.GetFullPath(pluginConfigDirectory));
             current is not null;
             current = current.Parent)
        {
            if (string.Equals(
                    current.Name,
                    ChineseLauncherName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ChineseLauncherName;
            }

            if (string.Equals(
                    current.Name,
                    GlobalLauncherName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return GlobalLauncherName;
            }
        }

        return null;
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
