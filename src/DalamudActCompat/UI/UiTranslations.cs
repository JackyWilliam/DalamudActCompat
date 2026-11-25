using System.Collections.Concurrent;
using System.Text.Json;

namespace DalamudActCompat.UI;

internal static class UiTranslations
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>?> Catalogs =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> SuffixedCache =
        new(StringComparer.Ordinal);

    internal static string Get(string language, string english)
    {
        if (language == "en" || language == "zh-CN") return english;
        var catalog = TryGetCatalog(language);
        if (catalog is null) return english;

        // ImGui's hidden suffix identifies the control. It must survive language
        // changes byte-for-byte, including any dynamic IDs supplied by callers.
        var hidden = english.IndexOf("##", StringComparison.Ordinal);
        if (hidden < 0)
        {
            return catalog.TryGetValue(english, out var translated) ? translated : english;
        }

        var cache = SuffixedCache.GetOrAdd(language, static _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
        if (cache.TryGetValue(english, out var cached))
        {
            return cached;
        }

        var key = english[..hidden];
        var result = catalog.TryGetValue(key, out var translatedPrefix)
            ? translatedPrefix + english[hidden..]
            : english;
        if (cache.Count < 4096)
        {
            cache.TryAdd(english, result);
        }
        return result;
    }

    internal static IReadOnlyDictionary<string, string> Catalog(string language)
        => TryGetCatalog(language)
           ?? throw new KeyNotFoundException($"No UI translation catalog exists for '{language}'.");

    private static IReadOnlyDictionary<string, string>? TryGetCatalog(string language)
    {
        if (Catalogs.TryGetValue(language, out var existing))
        {
            return existing;
        }

        return Catalogs.GetOrAdd(language, static lang =>
        {
            using var stream = typeof(UiTranslations).Assembly
                .GetManifestResourceStream($"DACT.Locales.{lang}.json");
            return stream is null
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        });
    }
}
