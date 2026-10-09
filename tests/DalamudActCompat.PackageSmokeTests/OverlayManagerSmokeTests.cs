using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Overlay;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static class OverlayManagerSmokeTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Set(object target, string field, object? value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Visible(HtmlOverlayWindowSettings settings, bool visible) => typeof(HtmlOverlayWindowSettings).GetProperty("IsVisible")!.SetValue(settings, visible);

    private sealed class Fixture
    {
        internal readonly PluginConfiguration Config = new();
        internal readonly ControlCenterWindow Window = (ControlCenterWindow)RuntimeHelpers.GetUninitializedObject(typeof(ControlCenterWindow));
        internal readonly List<ActOverlayTemplate> Templates =
        [
            new(SelfHostedActRuntime.CactbotAlertsOverlayName, "https://overlayplugin.github.io/cactbot/ui/raidboss/raidboss.html?alerts=1&timeline=0", 900, 500, true),
            new(SelfHostedActRuntime.CactbotTimelineOverlayName, "https://overlayplugin.github.io/cactbot/ui/raidboss/raidboss.html?alerts=0&timeline=1", 900, 500, true),
            new("Kagerou", "https://example.invalid/kagerou", 900, 500),
        ];
        internal readonly List<string> Opened = [], Closed = [], Deleted = [], Applied = [];
        internal CactbotOperationStatus Operation = new(CactbotOperationState.Ready);
        internal int SettingsOpened, InstallerOpened;
        internal bool OpenSucceeds = true;
        internal Fixture()
        {
            Config.OverlayWindows.Clear();
            Config.RegisterOverlayWindow(SelfHostedActRuntime.CactbotAlertsOverlayName);
            Config.RegisterOverlayWindow(SelfHostedActRuntime.CactbotTimelineOverlayName);
            Config.GetOverlayWindowSettings(SelfHostedActRuntime.CactbotRadarOverlayName); // Defaults are not registration.
            Config.RegisterOverlayWindow("Kagerou");
            Config.RegisterOverlayWindow("custom").SourceUrl = "https://example.invalid/original#/route";
            Config.GetOverlayWindowSettings("custom").DisplayName = "自定义网页";
            Set(Window, "configuration", Config); Set(Window, "text", new UiText(Config));
            Set(Window, "parserStatus", new ParserStatus(ParserState.Running, "Running", DateTimeOffset.UnixEpoch));
            Set(Window, "getOverlayTemplates", (Func<IReadOnlyList<ActOverlayTemplate>>)(() => Templates));
            Set(Window, "isCactbotInstalled", (Func<bool>)(() => true));
            Set(Window, "getCactbotOperationStatus", (Func<CactbotOperationStatus>)(() => Operation));
            Set(Window, "openCactbotSettings", (Action)(() => SettingsOpened++));
            Set(Window, "selectCactbotPackage", (Action)(() => InstallerOpened++));
            Set(Window, "setHideHtmlOverlaysWhenUnfocused", (Action<bool>)(v => Config.HideHtmlOverlaysWhenGameUnfocused = v));
            Set(Window, "saveConfiguration", (Action)(() => { }));
            Set(Window, "openHtmlOverlay", (Func<string, bool>)(name =>
            {
                Opened.Add(name);
                if (!OpenSucceeds) return false;
                var settings = Config.RegisterOverlayWindow(name);
                Visible(settings, true); settings.OpenOnStartup = true; settings.IsUserHidden = false;
                return true;
            }));
            Set(Window, "closeHtmlOverlay", (Action<string>)(name => { Closed.Add(name); Visible(Config.GetOverlayWindowSettings(name), false); }));
            Set(Window, "deleteHtmlOverlay", (Action<string>)(name =>
            {
                Deleted.Add(name);
                if (SelfHostedActRuntime.IsCactbotOverlayName(name)) Config.GetOverlayWindowSettings(name).ResetRegistration();
                else Config.OverlayWindows.Remove(name);
            }));
            Set(Window, "applyOverlayWindowSettings", (Action<string>)(name => Applied.Add(name)));
        }
    }

    internal static void Run(bool native = false)
    {
        var f = new Fixture(); var w = f.Window;
        Check(w.RegisteredOverlayNames().Length == 4 && !w.RegisteredOverlayNames().Contains(SelfHostedActRuntime.CactbotRadarOverlayName),
            "Reading Cactbot defaults registered an unopened overlay.");
        f.Config.GetOverlayWindowSettings(SelfHostedActRuntime.CactbotAlertsOverlayName).ResetRegistration();
        Check(w.RegisteredOverlayNames().Length == 3, "Reset Cactbot entry still appears in the unified list.");
        var custom = f.Config.GetOverlayWindowSettings("custom");
        Check(ControlCenterWindow.CanOpenManagedOverlay("custom", custom, []), "Saved custom URL depends on a loaded template.");
        Check(!ControlCenterWindow.CanOpenManagedOverlay(SelfHostedActRuntime.CactbotTimelineOverlayName, custom, []), "Missing Cactbot page can fall back to custom/remote URL.");
        Check(ControlCenterWindow.ManagedOverlaySource(f.Templates[1], custom) == "cactbot/ui/raidboss/raidboss.html",
            "Cactbot source UI presents an upstream address as the active local page.");
        Check(ControlCenterWindow.ManagedOverlaySource(f.Templates[2], custom) == f.Templates[2].Uri,
            "Template source was replaced by a stale custom URL.");
        var original = custom.SourceUrl;
        Check(!w.SaveManagedOverlaySource("custom", "javascript:bad") && custom.SourceUrl == original && f.Opened.Count == 0,
            "Invalid URL changed saved state or opened a page.");
        Check(!w.SaveManagedOverlaySource("Kagerou", "https://example.invalid/override") &&
              !w.SaveManagedOverlaySource(SelfHostedActRuntime.CactbotTimelineOverlayName, "https://example.invalid/override"),
            "Editing a fixed template source bypassed its original resolver.");
        Visible(custom, true); custom.OpenOnStartup = false; custom.IsUserHidden = true;
        custom.Hotkeys.Add(new() { Key = 65, Modifiers = OverlayHotkeyModifiers.Alt });
        Check(w.SaveManagedOverlaySource("custom", "https://example.invalid/new#/route"), "Valid URL could not be saved.");
        Check(f.Opened.SequenceEqual(new[] { "custom" }) && f.Applied.Last() == "custom" && custom.IsUserHidden && !custom.OpenOnStartup && custom.Hotkeys.Count == 1,
            "Refreshing the source reset independent hiding/startup/key preferences or another overlay.");
        Visible(custom, false); f.Opened.Clear();
        Check(w.SaveManagedOverlaySource("custom", "file:///C:/overlays/test.html") && f.Opened.Count == 0,
            "Editing a closed overlay unexpectedly reopened it.");
        Visible(custom, true); f.OpenSucceeds = false;
        Check(w.SaveManagedOverlaySource("custom", "https://example.invalid/retry") && custom.IsUserHidden && !custom.OpenOnStartup,
            "A failed refresh lost the saved source or independent preferences.");
        if (native) NativeUi();
        Console.WriteLine("PASS unified overlay manager: registration/reset, local Cactbot source, URL validation and refresh, preserved startup/hiding/hotkeys, closed/failing overlays.");
    }

    private static unsafe void NativeUi()
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI")!;
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true); NativeLibrary.Load(library);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0x024f, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            Check(io.Fonts.Build(), "Overlay manager font failed.");
            var raster = new NativeUiRasterizer(io.Fonts); var f = new Fixture();
            using var service = new HtmlOverlayHotkeyService(); var editor = new OverlayHotkeyEditor(service, _ => false); f.Window.HotkeyEditor = editor;
            f.Config.GetOverlayWindowSettings("custom").Hotkeys = [new() { Key = 220, Modifiers = OverlayHotkeyModifiers.Alt }];
            var method = typeof(ControlCenterWindow).GetMethod("DrawOverlays", Private)!;
            var details = typeof(ControlCenterWindow).GetField("overlayDetailsPage", Private)!;
            var size = new Vector2(1180, 880);
            void Frame()
            {
                io.DisplaySize = size + new Vector2(40); editor.BeginFrame(); ImGui.NewFrame();
                DactTheme.SetCurrent(f.Config.Appearance, true, 3);
                using (DactTheme.PushFrame())
                {
                    ControlCenterWindow.PushTheme();
                    ImGui.SetNextWindowPos(new(20)); ImGui.SetNextWindowSize(size);
                    ImGui.PushStyleColor(ImGuiCol.WindowBg, DactTheme.Palette.Surface);
                    ImGui.Begin("Overlay manager - production UI", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
                    method.Invoke(f.Window, null);
                    ImGui.End(); ImGui.PopStyleColor(); ControlCenterWindow.PopTheme();
                }
                ImGui.Render(); editor.EndFrame();
            }
            void Save(string name)
            {
                for (var i = 0; i < 50; i++) Frame();
                if (Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT") is { } output)
                    raster.Save(ImGui.GetDrawData(), Path.Combine(output, name + ".png"));
            }
            Save("DACT-正式界面-常规");
            Set(f.Window, "selectedCreatedOverlay", "custom");
            details.SetValue(f.Window, Enum.ToObject(details.FieldType, 1)); Save("DACT-正式界面-快捷键");
            details.SetValue(f.Window, Enum.ToObject(details.FieldType, 2)); Save("DACT-正式界面-来源");
            // Exercise the actual ImGui popup lifecycle with native mouse input
            // to catch ID mismatches between OpenPopup and BeginPopupModal.
            details.SetValue(f.Window, Enum.ToObject(details.FieldType, 0));
            io.AddMousePosEvent(1115, 59); Frame(); io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame();
            Check(ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId), "Add overlay popup did not open from the real page.");
            Save("DACT-正式界面-添加");
            f.Config.UiLanguage = "de"; size = new(720, 800); Save("DACT-正式界面-窄窗添加");
            var popup = new ImGuiWindowPtr(context.OpenPopupStack[0].Window);
            Check(popup.Pos.X >= 0 && popup.Pos.X + popup.Size.X <= io.DisplaySize.X,
                "Resizing the viewport stranded the add dialog outside its bounds.");
            var cancel = popup.Pos + new Vector2(40, popup.Size.Y - popup.WindowPadding.Y - ImGui.GetFrameHeight() * .5f);
            io.AddMousePosEvent(cancel.X, cancel.Y); Frame(); io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame();
            Check(!ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId), "Cancel did not close the actual add dialog.");
            Save("DACT-正式界面-窄窗德语");
            f.Operation = new(CactbotOperationState.Error, "Test installation error"); f.Templates.Clear();
            Set(f.Window, "selectedCreatedOverlay", SelfHostedActRuntime.CactbotTimelineOverlayName); Frame();
            Console.WriteLine("PASS unified overlay native UI: actual page, add popup, source/hotkey tabs, narrow translated layout and missing resources.");
        }
        finally { DactTheme.SetCurrent(new(), false, 0); ImGui.DestroyContext(context); }
    }
}
