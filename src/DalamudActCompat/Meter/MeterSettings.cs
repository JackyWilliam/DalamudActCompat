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

    public bool IsLocked { get; set; }

    public bool ClickThroughWhenLocked { get; set; }

    public bool AutoHideOutOfCombat { get; set; }

    public float BackgroundOpacity { get; set; } = 0.85f;

    public float FontScale { get; set; } = 1.0f;

    public int RefreshIntervalMs { get; set; } = 750;

    public DpsMetric DpsMetric { get; set; } = DpsMetric.EncDps;

    public MeterSortMode SortMode { get; set; } = MeterSortMode.Dps;

    public PlayerIdentityMode PlayerIdentityMode { get; set; } = PlayerIdentityMode.Original;

    public string LocalPlayerAlias { get; set; } = "自己";

    public bool ShowHeader { get; set; } = true;

    public bool CompactMode { get; set; }

    public bool ShowJob { get; set; } = true;

    public JobDisplayStyle JobDisplayStyle { get; set; } = JobDisplayStyle.Abbreviation;

    public bool ShowDamage { get; set; } = true;

    public bool ShowHps { get; set; } = true;

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
}
