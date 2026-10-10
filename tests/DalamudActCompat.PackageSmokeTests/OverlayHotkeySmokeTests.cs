using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Overlay;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Newtonsoft.Json;

internal static class OverlayHotkeySmokeTests
{
    private const OverlayHotkeyModifiers Modifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt | OverlayHotkeyModifiers.Shift;

    internal static void Run()
    {
        var configuration = new PluginConfiguration();
        var first = configuration.GetOverlayWindowSettings("custom-one");
        var second = configuration.GetOverlayWindowSettings("custom-two");
        first.IsUserHidden = true;
        first.Hotkeys.Add(new() { Key = (int)Keys.Oem5, Modifiers = OverlayHotkeyModifiers.Alt });
        var saved = JsonConvert.SerializeObject(configuration);
        Check(!saved.Contains("\"Status\":"), "Runtime registration state leaked into the profile.");
        var restored = JsonConvert.DeserializeObject<PluginConfiguration>(saved)!;
        Check(restored.GetOverlayWindowSettings("custom-one").IsUserHidden &&
            restored.GetOverlayWindowSettings("custom-one").Hotkeys.Single().Key == (int)Keys.Oem5 &&
            restored.GetOverlayWindowSettings("custom-two").Hotkeys.Count == 0,
            "Save/reload lost a shortcut or copied it to another overlay.");
        var snapshot = new PluginConfiguration(); snapshot.RestoreFrom(configuration.CreateSnapshot());
        Check(snapshot.GetOverlayWindowSettings("custom-one").Hotkeys.Single().Modifiers == OverlayHotkeyModifiers.Alt,
            "Cloud/profile snapshot lost modifiers.");
        var old = JsonConvert.DeserializeObject<HtmlOverlayWindowSettings>("{}")!;
        Check(old.Hotkeys.Count == 0 && !old.IsUserHidden, "Old profiles acquired unsolicited shortcuts/hiding.");
        first.ResetRegistration();
        Check(!first.IsUserHidden && first.Hotkeys.Count == 0, "Reset left shortcuts or manual hiding behind.");
        Check(HtmlOverlayHotkey.Format((int)Keys.Oem5, OverlayHotkeyModifiers.Alt).StartsWith("Alt + "), "OEM key cannot be named.");
        Check(OverlayHotkeyEditor.ReadModifiers(new HashSet<int> { 163, 164, 161, 92 }) == (OverlayHotkeyModifiers)15,
            "Left/right modifier capture failed.");
        Check(!HtmlOverlayHotkey.IsKeyboardKey(1) && !HtmlOverlayHotkey.IsKeyboardKey(17) &&
            HtmlOverlayHotkey.IsKeyboardKey((int)Keys.Oem5), "Modifier or mouse button was treated as the main key.");

        // Only reserve unused F13-F24 combinations; no physical key events, browser
        // interaction, focus changes or user configuration are injected by this test.
        var keys = Enumerable.Range((int)Keys.F13, 12).Where(CanReserve).Take(2).ToArray();
        Check(keys.Length == 2, "Two unused test-only hotkeys are required.");
        using (var service = new HtmlOverlayHotkeyService())
        {
            var one = new HtmlOverlayHotkey { Key = keys[0], Modifiers = Modifiers };
            var duplicate = new HtmlOverlayHotkey { Key = keys[0], Modifiers = Modifiers };
            var two = new HtmlOverlayHotkey { Key = keys[0], Modifiers = Modifiers, Action = OverlayHotkeyAction.ToggleOpen };
            first.Hotkeys = [one, duplicate]; second.Hotkeys = [two];
            service.Configure(configuration.OverlayWindows);
            Check(one.Status == OverlayHotkeyStatus.Registered && two.Status == OverlayHotkeyStatus.Registered && !CanReserve(keys[0]),
                "Global shortcut was not registered/shared with Windows.");
            var delivered = new List<(string Name, OverlayHotkeyAction Action)>();
            void Drain() => service.DispatchPending((name, action) => delivered.Add((name, action)));
            Send(service, keys[0]); Drain();
            Check(delivered.Count == 2 && delivered.Contains(("custom-one", OverlayHotkeyAction.ToggleVisibility)) &&
                delivered.Contains(("custom-two", OverlayHotkeyAction.ToggleOpen)),
                "One shared key double-toggled an overlay or failed to reach another overlay.");
            delivered.Clear();
            Send(service, keys[0]);
            first.Hotkeys.Clear(); second.Hotkeys.Clear(); service.Configure(configuration.OverlayWindows); Drain();
            Check(delivered.Count == 0 && CanReserve(keys[0]), "Deleted overlay bindings retained queued actions or OS registrations.");

            first.Hotkeys.Add(one); service.Configure(configuration.OverlayWindows);
            one.Enabled = false; service.Configure(configuration.OverlayWindows);
            Check(one.Status == OverlayHotkeyStatus.Disabled && CanReserve(keys[0]), "Disabling did not release the shortcut.");
            one.Enabled = true; service.Configure(configuration.OverlayWindows);
            service.Suspend(true);
            Check(CanReserve(keys[0]), "Key recording could not capture an already registered shortcut.");
            service.Suspend(false);
            Check(!CanReserve(keys[0]), "Finishing recording failed to restore shortcuts.");

            // The editor must cancel a recorder when its row/page no longer draws.
            var editor = new OverlayHotkeyEditor(service);
            typeof(OverlayHotkeyEditor).GetField("recording", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, one);
            service.Suspend(true); editor.BeginFrame(); editor.EndFrame();
            Check(!CanReserve(keys[0]), "Closing settings left global shortcuts suspended.");
            var held = new HashSet<int> { keys[0] };
            var releaseEditor = new OverlayHotkeyEditor(service, held.Contains);
            typeof(OverlayHotkeyEditor).GetField("releaseKey", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(releaseEditor, keys[0]);
            service.Suspend(true); releaseEditor.BeginFrame(); releaseEditor.EndFrame();
            Check(CanReserve(keys[0]), "Recording resumed shortcuts while the new key was still held.");
            held.Clear(); releaseEditor.EndFrame();
            Check(!CanReserve(keys[0]), "Releasing the recorded key did not resume shortcuts.");

            var nativeWindow = (Control)typeof(HtmlOverlayHotkeyService).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            var previousId = RegistrationId(nativeWindow, keys[0]);
            one.Key = keys[1]; service.Configure(configuration.OverlayWindows);
            SendMessage(nativeWindow.Handle, 0x0312, previousId, Pack(keys[0])); Drain();
            Check(delivered.Count == 0, "A stale WM_HOTKEY invoked a newly rebound action.");
            Send(service, keys[1]); Drain();
            Check(delivered.Count == 1 && delivered[0].Name == "custom-one", "Rebinding did not take effect.");

            // A competing application owns this key. A visible failure is preferable
            // to persisting a shortcut that silently does nothing.
            Check(RegisterHotKey(0, 0x6543, (uint)Modifiers, (uint)keys[0]), "Could not reserve the conflict fixture.");
            try
            {
                one.Key = keys[0]; service.Configure(configuration.OverlayWindows);
                Check(one.Status == OverlayHotkeyStatus.Unavailable, "OS shortcut conflict was not surfaced.");
            }
            finally { UnregisterHotKey(0, 0x6543); }
            service.Configure(configuration.OverlayWindows);
            Check(one.Status == OverlayHotkeyStatus.Registered, "A released conflict could not recover.");
            for (var cycle = 0; cycle < 1000; cycle++)
            {
                service.Suspend(true); service.Suspend(false);
            }
            Check(!CanReserve(keys[0]), "Repeated edit cycles exhausted the global registration.");
        }
        Check(CanReserve(keys[0]) && CanReserve(keys[1]), "Unloading leaked a global shortcut.");
        Console.WriteLine("PASS overlay hotkeys: persistence/old profiles/instance isolation/OEM modifiers/native Windows registration/sharing/deduplication/disabled/deleted/rebound/conflict/capture cancellation/1000 cycles/unload (WM_HOTKEY routing; no physical keys injected).");
    }

    private static int RegistrationId(Control window, int key)
        => (int)window.Invoke(() => ((Dictionary<int, int>)window.GetType().GetField("registered", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!)[key | ((int)Modifiers << 16)]);

    private static void Send(HtmlOverlayHotkeyService service, int key)
    {
        var window = (Control)typeof(HtmlOverlayHotkeyService).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        SendMessage(window.Handle, 0x0312, RegistrationId(window, key), Pack(key));
    }

    private static nint Pack(int key) => (nint)((key << 16) | (int)Modifiers);

    private static bool CanReserve(int key)
    {
        if (!RegisterHotKey(0, 0x6542, (uint)Modifiers, (uint)key)) return false;
        UnregisterHotKey(0, 0x6542);
        return true;
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
}
