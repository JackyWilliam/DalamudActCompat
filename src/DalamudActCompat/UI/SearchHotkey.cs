using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Plugin;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DalamudActCompat.UI;

// The 全局搜索 shortcut (default Ctrl+Shift+K). Dalamud hands ordinary keys to ImGui only while an
// ImGui text field is typing, so ImGui never sees the chord while the game has the keyboard (why
// Ctrl+K never worked in game). The chord is read from the OS key state once per frame instead,
// only while the game is the foreground window and the game's own chat or text input is unused.
internal sealed class SearchHotkey(PluginConfiguration configuration, Func<int, bool>? keyState = null)
{
    private const int ShiftKey = 0x10, ControlKey = 0x11, AltKey = 0x12, LeftWindows = 0x5B, RightWindows = 0x5C;
    private readonly Func<int, bool> isDown = keyState ?? IsKeyDown;
    private bool wasDown;

    // The settings page records a new chord: the old one must not fire meanwhile. Renewed every
    // frame the recorder is drawn, so leaving the page can never leave the shortcut switched off.
    internal int SuspendedThroughFrame { get; set; } = -1;

    internal static string Format(int key, OverlayHotkeyModifiers modifiers)
        => HtmlOverlayHotkey.Format(key, modifiers).Replace(" + ", "+", StringComparison.Ordinal);

    internal static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    // True once per press of the chord: holding it does not repeat. The held state is tracked
    // through a suspension too: a chord pressed while recording (the same one again, say) is
    // still held when recording ends and must not count as a fresh press.
    internal bool Poll(Func<bool> gameTextInputActive)
    {
        var key = configuration.SearchHotkeyKey;
        var suspended = ImGui.GetFrameCount() <= SuspendedThroughFrame;
        var down = configuration.SearchHotkeyActive() && HtmlOverlayHotkey.IsKeyboardKey(key) && isDown(key) &&
                   ModifiersMatch(configuration.SearchHotkeyModifiers);
        var pressed = down && !wasDown && !suspended;
        wasDown = down;
        return pressed && GameForegroundDetector.IsCurrentProcessForeground() && !gameTextInputActive();
    }

    // Exact match, so Ctrl+Shift+K does not also fire for Ctrl+K or Ctrl+Alt+Shift+K.
    internal OverlayHotkeyModifiers CurrentModifiers()
        => (isDown(ControlKey) ? OverlayHotkeyModifiers.Control : 0) |
           (isDown(ShiftKey) ? OverlayHotkeyModifiers.Shift : 0) |
           (isDown(AltKey) ? OverlayHotkeyModifiers.Alt : 0) |
           (isDown(LeftWindows) || isDown(RightWindows) ? OverlayHotkeyModifiers.Windows : 0);

    private bool ModifiersMatch(OverlayHotkeyModifiers wanted) => CurrentModifiers() == wanted;

    // The game's chat box (and its other text fields) own the keyboard while typing.
    internal static unsafe bool GameTextInputActive()
    {
        var module = RaptureAtkModule.Instance();
        return module is not null && module->IsTextInputActive();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
