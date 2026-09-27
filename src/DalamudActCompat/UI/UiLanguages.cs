namespace DalamudActCompat.UI;

internal static class UiLanguages
{
    internal static readonly (string Id, string Name)[] All =
    [
        ("zh-CN", "简体中文"), ("zh-TW", "繁體中文"), ("en", "English"),
        ("ja", "日本語"), ("de", "Deutsch"), ("fr", "Français"),
    ];

    // Accept locale variants from older/manual configs without coupling the UI
    // language to the game region or parser language.
    internal static string Normalize(string? language)
    {
        // This runs for every label on every frame; canonical config values must
        // not allocate a lowercased string each time the UI draws.
        if (language is "zh-CN" or "zh-TW" or "en" or "ja" or "de" or "fr") return language;
        return language?.Replace('_', '-').ToLowerInvariant() switch
        {
            "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" => "zh-TW",
            "en" or "en-us" or "en-gb" => "en",
            "ja" or "ja-jp" => "ja",
            "de" or "de-de" => "de",
            "fr" or "fr-fr" => "fr",
            _ => "zh-CN",
        };
    }

    internal static string Name(string language) => All.First(item => item.Id == Normalize(language)).Name;
}
