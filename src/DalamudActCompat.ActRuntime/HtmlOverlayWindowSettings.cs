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

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public bool IsEditing { get; private set; }

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
        IsEditing = editing;
        if (editing)
        {
            // Positioning needs both browser input and native drag handling. Keep this
            // temporary mode separate from the user's normal lock/click-through choices.
            IsClickThrough = false;
            IsLocked = false;
        }
    }

    public void SetClickThrough(bool clickThrough)
    {
        IsClickThrough = clickThrough;
        if (clickThrough)
        {
            // An input-transparent window cannot also expose native edit gestures.
            IsEditing = false;
        }
    }

    public void SetLocked(bool locked)
    {
        IsLocked = locked;
        if (locked)
        {
            // Locking is a user preference, while edit mode is only a temporary tool.
            IsEditing = false;
        }
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
        IsEditing = false;
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
