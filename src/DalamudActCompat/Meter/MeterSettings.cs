namespace DalamudActCompat.Meter;

public enum MeterSortMode
{
    Dps,
    Hps,
    Damage,
    Deaths,
}

public sealed class MeterSettings
{
    public bool IsVisible { get; set; } = true;

    public bool IsLocked { get; set; }

    public bool ClickThroughWhenLocked { get; set; }

    public bool AutoHideOutOfCombat { get; set; }

    public float BackgroundOpacity { get; set; } = 0.85f;

    public float FontScale { get; set; } = 1.0f;

    public MeterSortMode SortMode { get; set; } = MeterSortMode.Dps;
}
