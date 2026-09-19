using Newtonsoft.Json;

namespace DalamudActCompat.UI;

public sealed class UiSkinSettings
{
    public string SelectedSkin { get; set; } = SkinCatalog.Default;

    // Only discoveries are portable preferences. Sponsor authority is never saved
    // in configuration or restored from a cloud backup.
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public HashSet<string> UnlockedEasterEggs { get; set; } = new(StringComparer.Ordinal);

    internal bool Normalize()
    {
        var changed = false;
        if (UnlockedEasterEggs is null)
        {
            UnlockedEasterEggs = new(StringComparer.Ordinal);
            changed = true;
        }
        changed |= UnlockedEasterEggs.RemoveWhere(id => !SkinCatalog.IsEasterEgg(id)) > 0;
        if (!SkinCatalog.All.Any(skin => skin.Id == SelectedSkin))
        {
            SelectedSkin = SkinCatalog.Default;
            changed = true;
        }
        return changed;
    }
}

internal sealed record SkinDefinition(string Id, string ChineseName, string EnglishName, int SponsorTier = 0, bool EasterEgg = false);

internal static class SkinCatalog
{
    public const string Default = "default";
    public const string Eorzea = "eorzea";
    public const string Jade = "jade";
    public const string Amethyst = "amethyst";
    public const string Amber = "amber";
    public const string NeonPink = "neon-pink";

    public static IReadOnlyList<SkinDefinition> All { get; } =
    [
        new(Default, "深海原色", "Deep Ocean"),
        new(Eorzea, "艾欧泽亚", "Eorzea", SponsorTier: 1),
        new(Jade, "森之青玉", "Forest Jade", EasterEgg: true),
        new(Amethyst, "月下紫晶", "Moonlit Amethyst", EasterEgg: true),
        new(Amber, "暮光琥珀", "Twilight Amber", EasterEgg: true),
        new(NeonPink, "荧光粉", "Neon Pink", EasterEgg: true),
    ];

    public static bool IsEasterEgg(string id) => All.Any(skin => skin.Id == id && skin.EasterEgg);

    public static bool IsAvailable(string id, UiSkinSettings settings, bool signedIn, int sponsorTier)
        => All.FirstOrDefault(skin => skin.Id == id) is { } skin &&
           (!skin.EasterEgg || settings.UnlockedEasterEggs.Contains(id)) &&
           (skin.SponsorTier == 0 || signedIn && sponsorTier >= skin.SponsorTier);

    // Keep the user's choice during a logout/reconnect, but render the default
    // until the current account is verified. An administrator role grants no skin.
    public static string Resolve(UiSkinSettings settings, bool signedIn, int sponsorTier)
        => IsAvailable(settings.SelectedSkin, settings, signedIn, sponsorTier)
            ? settings.SelectedSkin : Default;
}

internal sealed class SkinDiscoveries
{
    private int logoClicks;
    private int versionClicks;
    private int appearanceClicks;
    private long lastLogoClick = long.MinValue;
    private long lastVersionClick = long.MinValue;
    private long lastAppearanceClick = long.MinValue;
    private int visitedPages;

    public string? ClickLogo(UiSkinSettings settings, long now)
        => Click(settings, SkinCatalog.Jade, 10, now, ref logoClicks, ref lastLogoClick);

    public string? ClickVersion(UiSkinSettings settings, long now)
        => Click(settings, SkinCatalog.Amethyst, 7, now, ref versionClicks, ref lastVersionClick);

    public string? ClickAppearanceTitle(UiSkinSettings settings, long now)
        => Click(settings, SkinCatalog.NeonPink, 5, now, ref appearanceClicks, ref lastAppearanceClick);

    public string? VisitPage(UiSkinSettings settings, int page)
    {
        if (page is < 0 or >= 6) return null;
        visitedPages |= 1 << page;
        return visitedPages == 0b111111 && settings.UnlockedEasterEggs.Add(SkinCatalog.Amber)
            ? SkinCatalog.Amber : null;
    }

    private static string? Click(UiSkinSettings settings, string id, int target, long now, ref int count, ref long last)
    {
        if (settings.UnlockedEasterEggs.Contains(id)) return null;
        // A discovery is a deliberate short sequence, not months of ordinary clicks.
        if (last == long.MinValue || now < last || now - last > 3000) count = 0;
        last = now;
        count = Math.Min(target, count + 1);
        return count == target && settings.UnlockedEasterEggs.Add(id) ? id : null;
    }
}
