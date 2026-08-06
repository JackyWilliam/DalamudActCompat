namespace DalamudActCompat.ActRuntime;

public sealed class HtmlOverlayWindowSettings
{
    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public bool IsVisible { get; internal set; }

    public bool IsClickThrough { get; set; } = true;

    public bool IsLocked { get; set; } = true;

    public bool IsEditing
        => !IsClickThrough && !IsLocked;

    public float ZoomFactor { get; set; } = 1.0f;

    public string SourceUrl { get; set; } = string.Empty;

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
