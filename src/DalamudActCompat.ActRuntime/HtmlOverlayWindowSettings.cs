namespace DalamudActCompat.ActRuntime;

public sealed class HtmlOverlayWindowSettings
{
    public bool IsClickThrough { get; set; } = true;

    public bool IsLocked { get; set; } = true;

    public bool IsEditing
        => !IsClickThrough && !IsLocked;

    public float ZoomFactor { get; set; } = 1.0f;

    public int? Left { get; set; }

    public int? Top { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public void SetEditing(bool editing)
    {
        IsClickThrough = !editing;
        IsLocked = !editing;
    }
}
