using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Overlay;

namespace DalamudActCompat.UI;

internal sealed class OverlayHotkeyEditor(HtmlOverlayHotkeyService service, Func<int, bool>? keyState = null)
{
    private readonly Func<int, bool> isKeyDown = keyState ?? (key => (GetAsyncKeyState(key) & 0x8000) != 0);
    private HtmlOverlayHotkey? recording;
    private HashSet<int> previousKeys = [];
    private bool recordingDrawn;
    private int releaseKey;

    internal void BeginFrame() => recordingDrawn = false;
    internal void EndFrame()
    {
        // Register only after the recorded key is released; its first keyboard
        // repeat must not immediately trigger the shortcut the user just assigned.
        if (releaseKey != 0)
        {
            if (!isKeyDown(releaseKey))
            {
                releaseKey = 0;
                service.Suspend(false);
            }
            return;
        }
        // Closing settings or navigating to another overlay must not leave all
        // global shortcuts suspended by an invisible key recorder.
        if (recording is not null && !recordingDrawn) Cancel();
    }

    private void Cancel()
    {
        recording = null;
        service.Suspend(false);
    }

    internal bool Draw(string overlayKey, HtmlOverlayWindowSettings settings, UiText text)
    {
        var changed = false;
        settings.Hotkeys ??= [];
        ImGui.PushID("overlay-hotkeys-" + overlayKey);
        ImGui.Spacing();
        ImGui.TextUnformatted(text.Get("快捷键", "Hotkeys"));
        var hidden = settings.IsUserHidden;
        if (ImGui.Checkbox(text.Get("隐藏悬浮窗", "Hide overlay"), ref hidden))
        {
            settings.IsUserHidden = hidden;
            changed = true;
        }
        foreach (var binding in settings.Hotkeys.ToArray())
        {
            ImGui.PushID(binding.Id.ToString());
            var enabled = binding.Enabled;
            if (ImGui.Checkbox(text.Get("启用", "Enabled"), ref enabled))
            {
                binding.Enabled = enabled;
                changed = true;
            }
            ImGui.SameLine();
            var label = ReferenceEquals(recording, binding)
                ? text.Get("请按组合键（Esc 取消）", "Press a shortcut (Esc cancels)")
                : HtmlOverlayHotkey.Format(binding.Key, binding.Modifiers);
            if (DactTheme.Button(string.IsNullOrEmpty(label) ? text.Get("设置快捷键", "Set hotkey") : label))
            {
                service.Suspend(true);
                releaseKey = 0;
                recording = binding;
                previousKeys = ReadKeys();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(230);
            if (DactTheme.BeginCombo("##action", ActionLabel(binding.Action, text)))
            {
                foreach (var action in Enum.GetValues<OverlayHotkeyAction>())
                    if (ImGui.Selectable(ActionLabel(action, text), binding.Action == action))
                    {
                        binding.Action = action;
                        changed = true;
                    }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (DactTheme.Button(text.Get("删除", "Delete")))
            {
                if (ReferenceEquals(recording, binding)) Cancel();
                settings.Hotkeys.Remove(binding);
                changed = true;
            }
            if (ReferenceEquals(recording, binding))
            {
                recordingDrawn = true;
                var keys = ReadKeys();
                if (keys.Contains(27)) Cancel();
                else
                {
                    var key = keys.Except(previousKeys).FirstOrDefault(HtmlOverlayHotkey.IsKeyboardKey);
                    if (key != 0)
                    {
                        binding.Key = key;
                        binding.Modifiers = ReadModifiers(keys);
                        binding.Enabled = true;
                        changed = true;
                        releaseKey = key;
                        recording = null;
                    }
                }
                previousKeys = keys;
            }
            else if (recording is null && releaseKey == 0 && binding.Enabled && binding.Key != 0 && binding.Status == OverlayHotkeyStatus.Unavailable)
                ImGui.TextWrapped(text.Get("快捷键不可用或已被其他程序占用，请换一个组合键。",
                    "This shortcut is unavailable or used by another application. Choose another combination."));
            ImGui.PopID();
        }
        if (DactTheme.Button(text.Get("添加快捷键", "Add hotkey")))
        {
            var binding = new HtmlOverlayHotkey();
            settings.Hotkeys.Add(binding);
            service.Suspend(true);
            releaseKey = 0;
            recording = binding;
            recordingDrawn = true;
            previousKeys = ReadKeys();
            changed = true;
        }
        ImGui.TextWrapped(text.Get("显示／隐藏保留网页运行；开启／关闭与上方按钮一致。快捷键在游戏后台时也生效。",
            "Show/hide keeps the page running; open/close follows the button above. Hotkeys also work while the game is in the background."));
        ImGui.PopID();
        return changed;
    }

    internal static string ActionLabel(OverlayHotkeyAction action, UiText text) => action switch
    {
        OverlayHotkeyAction.ToggleOpen => text.Get("切换悬浮窗 开启／关闭", "Toggle overlay open/closed"),
        OverlayHotkeyAction.ToggleClickThrough => text.Get("切换鼠标穿透", "Toggle click-through"),
        OverlayHotkeyAction.ToggleLock => text.Get("切换位置锁定", "Toggle position lock"),
        _ => text.Get("切换悬浮窗 显示／隐藏", "Toggle overlay shown/hidden"),
    };

    internal static OverlayHotkeyModifiers ReadModifiers(IReadOnlySet<int> keys)
        => (keys.Contains(17) || keys.Contains(162) || keys.Contains(163) ? OverlayHotkeyModifiers.Control : 0) |
           (keys.Contains(18) || keys.Contains(164) || keys.Contains(165) ? OverlayHotkeyModifiers.Alt : 0) |
           (keys.Contains(16) || keys.Contains(160) || keys.Contains(161) ? OverlayHotkeyModifiers.Shift : 0) |
           (keys.Contains(91) || keys.Contains(92) ? OverlayHotkeyModifiers.Windows : 0);

    private HashSet<int> ReadKeys()
        => Enumerable.Range(8, 247).Where(isKeyDown).ToHashSet();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
