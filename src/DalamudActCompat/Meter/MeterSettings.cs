namespace DalamudActCompat.Meter;

using System.Numerics;

public enum MeterSortMode
{
    Dps,
    Hps,
    // Retained so existing JSON values remain readable; normalized to DPS at runtime.
    Damage,
    // Retained so existing JSON values remain readable; normalized to DPS at runtime.
    Deaths,
}

public static class MeterSortModeOptions
{
    public static IReadOnlyList<MeterSortMode> Supported { get; } =
        [MeterSortMode.Dps, MeterSortMode.Hps];

    public static MeterSortMode Normalize(MeterSortMode mode)
        => mode == MeterSortMode.Hps ? MeterSortMode.Hps : MeterSortMode.Dps;
}

public enum DpsMetric
{
    Dps,
    EncDps,
    ExtDps,
    Rdps,
}

public enum PlayerIdentityMode
{
    Original,
    Job,
    Anonymous,
}

public enum JobDisplayStyle
{
    Abbreviation,
    ChineseAbbreviation,
    MinimalIcon,
    ClassicIcon,
    FlatIcon,
}

public sealed class MeterSettings
{
    private static readonly Vector4 LegacyLocalPlayerColor =
        new(0.25f, 0.42f, 0.55f, 0.45f);

    public static readonly Vector4 DefaultLocalPlayerColor =
        new(0x8B / 255f, 0x57 / 255f, 0x33 / 255f, 0x73 / 255f);

    public bool IsVisible { get; set; } = true;

    public MeterWindowProfile ClassicWindow { get; set; } = new()
    {
        IsEnabled = true,
        Slots = MeterSlotDefaults.CreateClassic(),
    };

    public MeterWindowProfile HorizontalWindow { get; set; } = new()
    {
        Slots = MeterSlotDefaults.CreateHorizontal(),
        ItemWidth = 220,
    };

    public MeterWindowProfile RoleSplitWindow { get; set; } = new()
    {
        Slots = MeterSlotDefaults.CreateRoleSplit(),
        ItemWidth = 250,
    };

    public bool IsLocked { get; set; }

    public bool ClickThroughWhenLocked { get; set; }

    public bool AutoHideOutOfCombat { get; set; }

    public MeterPreset Preset { get; set; } = MeterPreset.CurrentDefault;

    public string SelectedCustomStyleId { get; set; } = string.Empty;

    public List<MeterCustomStyle> CustomStyles { get; set; } = [];

    public float BackgroundOpacity { get; set; } = 0.85f;

    public float FontScale { get; set; } = 1.0f;

    public int RefreshIntervalMs { get; set; } = 750;

    public DpsMetric DpsMetric { get; set; } = DpsMetric.Rdps;

    public MeterSortMode SortMode { get; set; } = MeterSortMode.Dps;

    public PlayerIdentityMode PlayerIdentityMode { get; set; } = PlayerIdentityMode.Original;

    public string LocalPlayerAlias { get; set; } = "自己";

    public bool ShowHeader { get; set; } = true;

    public bool CompactMode { get; set; }

    public float ExpandedWindowWidth { get; set; } = 500;

    public float ExpandedWindowHeight { get; set; } = 420;

    public bool ShowJob { get; set; } = true;

    public bool ShowRank { get; set; } = true;

    public bool ShowPlayerName { get; set; } = true;

    public JobDisplayStyle JobDisplayStyle { get; set; } = JobDisplayStyle.Abbreviation;

    public bool ShowFflogs { get; set; } = true;

    public bool ShowDps { get; set; } = true;

    public bool ShowRdps { get; set; }

    public bool ShowDamagePercent { get; set; } = true;

    public bool ShowTotalDamage { get; set; } = true;

    public bool ShowTotalHealing { get; set; }

    public bool ShowHighestDamage { get; set; } = true;

    public bool ShowDeaths { get; set; } = true;

    public bool ShowCriticalHitRate { get; set; } = true;

    public bool ShowDirectHitRate { get; set; }

    public bool ShowCriticalDirectHitRate { get; set; } = true;

    // Retained for old configuration JSON. The one-line meter now exposes explicit columns.
    public bool ShowDamage { get; set; } = true;

    public bool ShowHps { get; set; }

    // Retained for old configuration JSON. The one-line meter now exposes explicit columns.
    public bool ShowHealing { get; set; } = true;

    public Vector4 LocalPlayerColor { get; set; } = DefaultLocalPlayerColor;

    internal bool MigrateLegacyLocalPlayerColor()
    {
        if (Vector4.DistanceSquared(LocalPlayerColor, LegacyLocalPlayerColor) > 0.000001f)
        {
            return false;
        }

        LocalPlayerColor = DefaultLocalPlayerColor;
        return true;
    }

    internal bool NormalizeCustomization()
    {
        var changed = false;
        ClassicWindow ??= new MeterWindowProfile
        {
            IsEnabled = true,
            Slots = MeterSlotDefaults.CreateClassic(),
        };
        HorizontalWindow ??= new MeterWindowProfile
        {
            Slots = MeterSlotDefaults.CreateHorizontal(),
            ItemWidth = 220,
        };
        RoleSplitWindow ??= new MeterWindowProfile
        {
            Slots = MeterSlotDefaults.CreateRoleSplit(),
            ItemWidth = 250,
        };
        changed |= ClassicWindow.Normalize(MeterSlotDefaults.CreateClassic());
        changed |= HorizontalWindow.Normalize(MeterSlotDefaults.CreateHorizontal());
        changed |= RoleSplitWindow.Normalize(MeterSlotDefaults.CreateRoleSplit());
        CustomStyles ??= [];
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in CustomStyles)
        {
            changed |= style.Normalize();
            if (!usedIds.Add(style.Id))
            {
                style.Id = Guid.NewGuid().ToString("N");
                usedIds.Add(style.Id);
                changed = true;
            }
        }

        if (Preset == MeterPreset.Custom &&
            !CustomStyles.Any(style => string.Equals(
                style.Id,
                SelectedCustomStyleId,
                StringComparison.OrdinalIgnoreCase)))
        {
            Preset = MeterPreset.CurrentDefault;
            SelectedCustomStyleId = string.Empty;
            changed = true;
        }

        return changed;
    }

    internal bool MigrateIndependentWindows()
    {
        ClassicWindow.IsEnabled = Preset == MeterPreset.CurrentDefault;
        HorizontalWindow.IsEnabled = Preset is MeterPreset.HorizontalTransparent or MeterPreset.Custom;
        RoleSplitWindow.IsEnabled = Preset == MeterPreset.RoleSplit;

        if (Preset == MeterPreset.Custom && GetSelectedCustomStyle() is { } customStyle)
        {
            // Preserve the user's chosen metrics, but intentionally discard coordinates:
            // the new editor auto-arranges stable slots and cannot create overlaps.
            HorizontalWindow.Slots = customStyle.Slots
                .Select(static slot => slot.Clone())
                .ToList();
        }

        MigrateProfile(ClassicWindow);
        MigrateProfile(HorizontalWindow);
        MigrateProfile(RoleSplitWindow);
        if (ShowDps && DpsMetric == DpsMetric.Rdps)
        {
            // The former single DPS column displayed the configured rate. Preserve
            // that visible value while allowing DPS and rDPS to coexist from now on.
            ShowDps = false;
            ShowRdps = true;
            foreach (var slot in ClassicWindow.Slots)
            {
                if (slot.Metric == MeterSlotMetric.Dps)
                {
                    slot.Visible = false;
                }
                else if (slot.Metric == MeterSlotMetric.Rdps)
                {
                    slot.Visible = true;
                }
            }
        }
        return true;

        void MigrateProfile(MeterWindowProfile profile)
        {
            // The old single window owned these settings. Copying them to every new
            // window avoids an unexpected unlock or auto-hide change after migration.
            profile.IsLocked = IsLocked;
            profile.ClickThroughWhenLocked = ClickThroughWhenLocked;
            profile.AutoHideOutOfCombat = AutoHideOutOfCombat;
            profile.ShowHeader = ShowHeader;
            profile.FontScale = FontScale;
            profile.SortMode = MeterSortModeOptions.Normalize(SortMode);
        }
    }

    public MeterCustomStyle? GetSelectedCustomStyle()
        => Preset == MeterPreset.Custom
            ? CustomStyles.FirstOrDefault(style => string.Equals(
                style.Id,
                SelectedCustomStyleId,
                StringComparison.OrdinalIgnoreCase))
            : null;
}
