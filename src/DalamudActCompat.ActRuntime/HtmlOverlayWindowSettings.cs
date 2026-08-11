namespace DalamudActCompat.ActRuntime;

public enum OverlayConnectionMode
{
    Auto,
    OverlayPlugin,
    ActWebSocket,
    Original,
}

public enum OverlayConnectionState
{
    None,
    Detecting,
    Retrying,
    Connected,
    Failed,
}

public sealed class HtmlOverlayWindowSettings
{
    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public bool IsVisible { get; internal set; }

    public bool OpenOnStartup { get; set; }

    public bool HasBeenOpened { get; set; }

    public bool IsClickThrough { get; set; } = true;

    public bool IsLocked { get; set; } = true;

    public bool IsEditing
        => !IsClickThrough && !IsLocked;

    public float ZoomFactor { get; set; } = 1.0f;

    public string SourceUrl { get; set; } = string.Empty;

    public OverlayConnectionMode ConnectionMode { get; set; } = OverlayConnectionMode.Auto;

    public OverlayConnectionMode? DetectedConnectionMode { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public OverlayConnectionState ConnectionState { get; internal set; }

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string ConnectionStateDetail { get; internal set; } = string.Empty;

    public int? Left { get; set; }

    public int? Top { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public void SetEditing(bool editing)
    {
        if (editing)
        {
            IsClickThrough = false;
            IsLocked = false;
            return;
        }

        // Keep browser input enabled after positioning. Users can explicitly
        // enable click-through when they want mouse input to reach the game.
        IsLocked = true;
    }

    public void ResetConnectionDetection()
    {
        DetectedConnectionMode = null;
        ConnectionState = OverlayConnectionState.None;
        ConnectionStateDetail = string.Empty;
    }

    public void ResetRegistration()
    {
        IsVisible = false;
        OpenOnStartup = false;
        HasBeenOpened = false;
        IsClickThrough = true;
        IsLocked = true;
        ZoomFactor = 1.0f;
        SourceUrl = string.Empty;
        ConnectionMode = OverlayConnectionMode.Auto;
        DetectedConnectionMode = null;
        ConnectionState = OverlayConnectionState.None;
        ConnectionStateDetail = string.Empty;
        Left = null;
        Top = null;
        Width = null;
        Height = null;
    }
}
