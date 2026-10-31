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
        // IDs this build has never heard of come from a newer client sharing the same
        // cloud backup. Keep them so restoring here and uploading again cannot reset that
        // device's choice; Resolve already renders the default for anything unavailable.
        changed |= UnlockedEasterEggs.RemoveWhere(id =>
            string.IsNullOrWhiteSpace(id) || SkinCatalog.IsKnown(id) && !SkinCatalog.IsEasterEgg(id)) > 0;
        if (string.IsNullOrWhiteSpace(SelectedSkin))
        {
            SelectedSkin = SkinCatalog.Default;
            changed = true;
        }
        return changed;
    }
}

internal sealed record SkinDefinition(string Id, string ChineseName, string EnglishName, int SponsorTier = 0, bool EasterEgg = false, bool Ultimate = false);

internal static class SkinCatalog
{
    public const string Default = "default";
    public const string Eorzea = "eorzea";
    // Entitlement and its UI copy share one threshold; local saved skins grant no access.
    public const int EorzeaSponsorTier = 3;
    public const string Jade = "jade";
    public const string Amethyst = "amethyst";
    public const string Amber = "amber";
    // Display names may change; the saved ID keeps existing and cloud-restored discoveries valid.
    public const string NeonPink = "neon-pink";
    public const string LiquidGlass = "liquid-glass";
    public const string Obsidian = "obsidian";
    public const string RainyWindow = "rainy-window";
    public const string UltimateBahamut = "ultimate-ucob";
    public const string UltimateWeapon = "ultimate-uwu";
    public const string UltimateAlexander = "ultimate-tea";
    public const string UltimateDragonsong = "ultimate-dsr";
    public const string UltimateOmega = "ultimate-top";
    public const string UltimateFutures = "ultimate-fru";

    // The active account owns a permanent reward. CloudClientService merges server
    // grants and its account-bound outbox; restoring ordinary preferences grants nothing.
    internal static Func<bool> UltimateUnlocked { get; set; } = () => false;

    public static IReadOnlyList<SkinDefinition> All { get; } =
    [
        new(Default, "深海原色", "Deep Ocean"),
        new(Eorzea, "艾欧泽亚", "Eorzea", SponsorTier: EorzeaSponsorTier),
        new(Jade, "森之青玉", "Forest Jade", EasterEgg: true),
        new(Amethyst, "月下紫晶", "Moonlit Amethyst", EasterEgg: true),
        new(Amber, "暮光琥珀", "Twilight Amber", EasterEgg: true),
        new(NeonPink, "绯梦霓光", "Neon Reverie", EasterEgg: true),
        new(LiquidGlass, "澄光流璃", "Lucent Glass", EasterEgg: true),
        new(Obsidian, "无相玄墨", "Obsidian Void", EasterEgg: true),
        new(RainyWindow, "听雨窗", "Rain on Glass", EasterEgg: true),
        new(UltimateBahamut, "巴哈姆特绝境战", "The Unending Coil of Bahamut", Ultimate: true),
        new(UltimateWeapon, "究极神兵绝境战", "The Weapon's Refrain", Ultimate: true),
        new(UltimateAlexander, "亚历山大绝境战", "The Epic of Alexander", Ultimate: true),
        new(UltimateDragonsong, "幻想龙诗绝境战", "Dragonsong's Reprise", Ultimate: true),
        new(UltimateOmega, "欧米茄绝境验证战", "The Omega Protocol", Ultimate: true),
        new(UltimateFutures, "光暗未来绝境战", "Futures Rewritten", Ultimate: true),
    ];

    public static bool IsKnown(string id) => All.Any(skin => skin.Id == id);
    public static bool IsEasterEgg(string id) => All.Any(skin => skin.Id == id && skin.EasterEgg);
    public static bool IsUltimate(string id) => All.Any(skin => skin.Id == id && skin.Ultimate);

    public static bool IsAvailable(string id, UiSkinSettings settings, bool signedIn, int sponsorTier)
        => All.FirstOrDefault(skin => skin.Id == id) is { } skin &&
           (!skin.EasterEgg || settings.UnlockedEasterEggs.Contains(id)) &&
           (skin.SponsorTier == 0 || signedIn && sponsorTier >= skin.SponsorTier) &&
           (!skin.Ultimate || signedIn && UltimateUnlocked());

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
    private int breadcrumbClicks;
    private int hintClicks;
    private int cloudStatusClicks;
    private long lastLogoClick = long.MinValue;
    private long lastVersionClick = long.MinValue;
    private long lastAppearanceClick = long.MinValue;
    private long lastBreadcrumbClick = long.MinValue;
    private long lastHintClick = long.MinValue;
    private long lastCloudStatusClick = long.MinValue;
    private int visitedPages;

    public string? ClickLogo(UiSkinSettings settings, long now)
        => Click(settings, SkinCatalog.Jade, 10, now, ref logoClicks, ref lastLogoClick);

    public string? ClickVersion(UiSkinSettings settings, long now)
        => Click(settings, SkinCatalog.Amethyst, 7, now, ref versionClicks, ref lastVersionClick);

    public string? ClickAppearanceTitle(UiSkinSettings settings, long now)
        => Click(settings, SkinCatalog.NeonPink, 5, now, ref appearanceClicks, ref lastAppearanceClick);

    public string? ClickAppearanceBreadcrumb(UiSkinSettings settings, long now)
        => Click(settings, SkinCatalog.LiquidGlass, 6, now, ref breadcrumbClicks, ref lastBreadcrumbClick);

    public string? ClickAppearanceHint(UiSkinSettings settings, long now)
        => Click(settings, SkinCatalog.Obsidian, 7, now, ref hintClicks, ref lastHintClick);

    public string? ClickCloudStatus(UiSkinSettings settings, long now)
        => Click(settings, SkinCatalog.RainyWindow, 8, now, ref cloudStatusClicks, ref lastCloudStatusClick);

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
