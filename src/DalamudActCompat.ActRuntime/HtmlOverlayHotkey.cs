using System.Windows.Forms;

namespace DalamudActCompat.ActRuntime;

[Flags]
public enum OverlayHotkeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }
public enum OverlayHotkeyAction { ToggleVisibility, ToggleOpen, ToggleClickThrough, ToggleLock }
public enum OverlayHotkeyStatus { Unassigned, Disabled, Registered, Unavailable }

public sealed class HtmlOverlayHotkey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public int Key { get; set; }
    public OverlayHotkeyModifiers Modifiers { get; set; }
    public OverlayHotkeyAction Action { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public OverlayHotkeyStatus Status { get; set; }

    public static bool IsKeyboardKey(int key)
        => key is > 7 and < 255 && Enum.IsDefined((Keys)key) &&
           key is not (16 or 17 or 18 or 91 or 92 or 160 or 161 or 162 or 163 or 164 or 165);

    public static string Format(int key, OverlayHotkeyModifiers modifiers)
    {
        if (!IsKeyboardKey(key)) return "";
        var parts = new List<string>();
        if (modifiers.HasFlag(OverlayHotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(OverlayHotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(OverlayHotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(OverlayHotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(((Keys)key).ToString());
        return string.Join(" + ", parts);
    }
}
