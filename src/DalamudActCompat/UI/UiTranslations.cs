using System.Text.Json;

namespace DalamudActCompat.UI;

internal static class UiTranslations
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalogs = Load();

    internal static string Get(string language, string english)
    {
        if (language == "en" || !Catalogs.TryGetValue(language, out var catalog)) return english;
        // ImGui's hidden suffix identifies the control. It must survive language
        // changes byte-for-byte, including any dynamic IDs supplied by callers.
        var hidden = english.IndexOf("##", StringComparison.Ordinal);
        var key = hidden < 0 ? english : english[..hidden];
        return catalog.TryGetValue(key, out var translated) ? translated + (hidden < 0 ? "" : english[hidden..]) : english;
    }

    internal static IReadOnlyDictionary<string, string> Catalog(string language) => Catalogs[language];

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in new[] { "zh-TW", "ja", "de", "fr" })
        {
            using var stream = typeof(UiTranslations).Assembly.GetManifestResourceStream($"DACT.Locales.{language}.json")!;
            result[language] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }
        return result;
    }
}
