using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Overlay;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static partial class OverlayManagerSmokeTests
{
    // Exercise production draw methods: catalog parity alone cannot detect a
    // translated checkbox escaping its child window or an unreachable modal.
    internal static unsafe void Acceptance()
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") ?? throw new InvalidOperationException("Native ImGui is required.");
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true); NativeLibrary.Load(library);
        var context = ImGui.CreateContext();
        var output = Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT")!;
        Directory.CreateDirectory(output);
        var results = new List<object>(); var failures = new List<string>();
        // Ultimate skins otherwise resolve to the default palette and would only repeat it.
        var previousUltimateUnlock = SkinCatalog.UltimateUnlocked;
        SkinCatalog.UltimateUnlocked = () => true;
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0x024f, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            Check(io.Fonts.Build(), "Acceptance font atlas failed.");
            var raster = new NativeUiRasterizer(io.Fonts); SkinSmokeTests.LoadGameTextures(raster);
            WrappedCheckboxInteraction();
            using var gpu = Environment.GetEnvironmentVariable("DACT_TEST_GPU_GLASS") == "1" ? new LiquidGlassSmokeTests() : null;
            gpu?.Install(raster);
            var f = new Fixture(); f.Config.Appearance.UnlockedEasterEggs.UnionWith(SkinCatalog.All.Where(s => s.EasterEgg).Select(s => s.Id));
            using var service = new HtmlOverlayHotkeyService();
            var editor = new OverlayHotkeyEditor(service, _ => false); f.Window.HotkeyEditor = editor;
            var custom = f.Config.GetOverlayWindowSettings("custom");
            custom.Hotkeys = [new() { Key = 220, Modifiers = OverlayHotkeyModifiers.Alt }];
            Set(f.Window, "isStatusVisible", (Func<bool>)(() => true));
            Set(f.Window, "getGameRegionSelection", (Func<GameRegionSelection>)(() => GameRegionResolver.Resolve(GameRegionMode.Auto, "Chinese", 4)));
            Set(f.Window, "setGameRegionMode", (Action<GameRegionMode>)(_ => { }));
            var draw = typeof(ControlCenterWindow).GetMethod("DrawOverlays", Private)!;
            var home = typeof(ControlCenterWindow).GetMethod("DrawOverview", Private)!;
            var details = typeof(ControlCenterWindow).GetField("overlayDetailsPage", Private)!;
            var creator = typeof(ControlCenterWindow).GetField("overlayCreatorPage", Private)!;
            var templates = f.Templates.ToArray();
            var size = new Vector2(1180, 880); var view = "general"; var openPopup = false;
            void Frame()
            {
                io.DisplaySize = size + new Vector2(40); editor.BeginFrame(); ImGui.NewFrame();
                DactTheme.SetCurrent(f.Config.Appearance, true, 3);
                gpu?.Renderer.BeginFrame(gpu.Device, DactTheme.CurrentSkin is SkinCatalog.LiquidGlass or SkinCatalog.RainyWindow);
                using (DactTheme.PushFrame())
                {
                    ControlCenterWindow.PushTheme();
                    ImGui.SetNextWindowPos(new(20)); ImGui.SetNextWindowSize(size);
                    ImGui.PushStyleColor(ImGuiCol.WindowBg, DactTheme.Palette.Surface);
                    ImGui.Begin("UI acceptance", DactTheme.WindowFlags(ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings));
                    DactTheme.DrawGameWindow();
                    // Stacked details can start below the viewport. Scroll the
                    // parent to them so native clipping cannot silently skip the page.
                    ImGui.SetScrollY(view != "home" && view != "list" && !view.StartsWith("add") && size.X < 760 * io.FontGlobalScale
                        ? ImGui.GetScrollMaxY() : 0);
                    if (openPopup) { ImGui.OpenPopup("###dact-add-overlay"); openPopup = false; }
                    if (view == "home") home.Invoke(f.Window, null); else draw.Invoke(f.Window, null);
                    ImGui.End(); ImGui.PopStyleColor(); ControlCenterWindow.PopTheme();
                }
                ImGui.Render(); editor.EndFrame();
                Check(context.ColorStack.Size == 0 && context.StyleVarStack.Size == 0, "Acceptance leaked theme scopes.");
            }
            foreach (var skin in SkinCatalog.All)
            {
                var only = Environment.GetEnvironmentVariable("DACT_ACCEPTANCE_SKIN");
                if (only is not null && skin.Id != only) continue;
                f.Config.Appearance.SelectedSkin = skin.Id;
                foreach (var language in UiLanguages.All)
                foreach (var width in new[] { 1180, 720 })
                foreach (var scale in new[] { 1f, 1.4f, 2f })
                {
                    f.Config.UiLanguage = language.Id; io.FontGlobalScale = scale; size = new(width, 880);
                    foreach (var scenario in new[] { "home", "list", "general", "cactbot", "hotkeys", "source", "edit-url", "rename", "missing", "add", "add-cactbot", "add-template", "add-url" })
                    {
                        view = scenario; var errors = new List<string>();
                        // Independent states prevent a previous language's scroll
                        // position, modal, or missing-resource case masking controls.
                        context.OpenPopupStack.Resize(0); context.BeginPopupStack.Resize(0);
                        f.Templates.Clear(); f.Templates.AddRange(templates); f.Operation = new(CactbotOperationState.Ready);
                        Set(f.Window, "selectedCreatedOverlay", scenario is "cactbot" or "missing" ? SelfHostedActRuntime.CactbotTimelineOverlayName : "custom");
                        details.SetValue(f.Window, Enum.ToObject(details.FieldType, scenario == "hotkeys" ? 1 : scenario is "source" or "edit-url" ? 2 : 0));
                        Set(f.Window, "editingOverlaySource", scenario == "edit-url" ? "custom" : null);
                        Set(f.Window, "overlayBeingRenamed", scenario == "rename" ? "custom" : null);
                        Set(f.Window, "overlayRenameValue", "Overlay name");
                        Set(f.Window, "overlaySourceDraft", custom.SourceUrl);
                        if (scenario == "missing") { f.Templates.Clear(); f.Operation = new(CactbotOperationState.Error, "Test installation error"); }
                        creator.SetValue(f.Window, scenario switch {
                            "add-cactbot" => Enum.ToObject(Nullable.GetUnderlyingType(creator.FieldType)!, 0),
                            "add-template" => Enum.ToObject(Nullable.GetUnderlyingType(creator.FieldType)!, 1),
                            "add-url" => Enum.ToObject(Nullable.GetUnderlyingType(creator.FieldType)!, 2), _ => null });
                        openPopup = scenario.StartsWith("add");
                        for (var frame = 0; frame < 5; frame++) Frame();
                        var detailsDrawn = scenario is "home" or "list" || scenario.StartsWith("add");
                        for (var i = 0; i < context.Windows.Size; i++)
                        {
                            var window = context.Windows[i]; if (!window.Active) continue;
                            var name = Marshal.PtrToStringUTF8((nint)window.Name) ?? "";
                            if (name.Contains("/overlay-details_") && !window.SkipItems && window.ContentSize.Y > 100) detailsDrawn = true;
                            if (!name.StartsWith("UI acceptance") && !name.Contains("dact-add-overlay")) continue;
                            var available = window.Size.X - window.WindowPadding.X * 2 - window.ScrollbarSizes.X;
                            if (window.ContentSize.X > available + 2)
                                errors.Add($"Horizontal overflow {name}: {window.ContentSize.X:0.0} > {available:0.0}");
                            if ((window.Flags & ImGuiWindowFlags.NoScrollbar) != 0 && name.Contains("overview-") &&
                                window.ContentSize.Y > window.Size.Y - window.WindowPadding.Y * 2 + 2)
                                errors.Add($"Unscrollable vertical overflow {name}: {window.ContentSize.Y:0.0} > {window.Size.Y - window.WindowPadding.Y * 2:0.0}");
                            if (name.Contains("dact-add-overlay") && (window.Pos.X < 0 || window.Pos.Y < 0 ||
                                window.Pos.X + window.Size.X > io.DisplaySize.X + 1 || window.Pos.Y + window.Size.Y > io.DisplaySize.Y + 1))
                                errors.Add("Modal escaped viewport");
                        }
                        if (!detailsDrawn) errors.Add("Details were clipped before production controls could draw");
                        var key = $"{skin.Id}_{language.Id}_{width}_{scale * 100:0}_{scenario}";
                        // All combinations execute. Capture every language/layout
                        // on the default skin plus every skin/page and every failure.
                        var capture = Environment.GetEnvironmentVariable("DACT_ACCEPTANCE_NO_CAPTURE") != "1" &&
                            (skin.Id == SkinCatalog.Default || (language.Id == "zh-CN" && width == 1180 && scale == 1) || errors.Count > 0);
                        if (capture) raster.Save(ImGui.GetDrawData(), Path.Combine(output, key + ".png"));
                        if (scenario == "add")
                        {
                            Check(context.OpenPopupStack.Size == 1, "Actual add dialog was not opened.");
                            var popup = new ImGuiWindowPtr(context.OpenPopupStack[0].Window);
                            var cancel = popup.Pos + new Vector2(popup.WindowPadding.X + 20,
                                popup.Size.Y - popup.WindowPadding.Y - ImGui.GetFrameHeight() * .5f);
                            io.AddMousePosEvent(cancel.X, cancel.Y); Frame();
                            io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame();
                            if (context.OpenPopupStack.Size != 0) errors.Add("Native Cancel click did not close modal");
                            io.AddMousePosEvent(-100, -100);
                        }
                        results.Add(new { key, skin = skin.Id, language = language.Id, width, fontPercent = scale * 100, scenario, errors, screenshot = capture ? key + ".png" : null });
                        failures.AddRange(errors.Select(e => key + ": " + e));
                    }
                }
                Console.WriteLine($"Acceptance {skin.Id}: {results.Count} cases, {failures.Count} layout violations.");
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "matrix.json"), JsonSerializer.Serialize(new { cases = results.Count, failures, results }, new JsonSerializerOptions { WriteIndented = true }));
            DactTheme.SetCurrent(new(), false, 0); ImGui.DestroyContext(context);
            SkinCatalog.UltimateUnlocked = previousUltimateUnlock;
        }
        Check(failures.Count == 0, $"UI acceptance found {failures.Count} layout violations; see matrix.json.");
        Console.WriteLine($"PASS UI acceptance: {results.Count} native skin/language/layout/page combinations.");
    }

    private static void WrappedCheckboxInteraction()
    {
        var io = ImGui.GetIO(); io.DisplaySize = new(800, 600);
        var label = "Hide overlays when the game loses focus##stable-setting";
        var value = false; var changed = 0; var hit = Vector2.Zero; var width = 250f;
        void Frame()
        {
            ImGui.NewFrame(); ImGui.SetNextWindowPos(new(20)); ImGui.SetNextWindowSize(new(width, 240));
            ImGui.Begin("wrapped-checkbox", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDecoration);
            var id = ImGui.GetID("###" + label);
            if (DactTheme.WrappedCheckbox(label, ref value)) changed++;
            Check(ImGui.GetCurrentContext().LastItemData.ID == id, "Wrapping changed a checkbox's saved navigation ID.");
            var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
            Check(max.X <= ImGui.GetWindowPos().X + width - ImGui.GetStyle().WindowPadding.X + 1, "Wrapped checkbox still escaped its window.");
            hit = new(min.X + 80, max.Y - 8); ImGui.End(); ImGui.Render();
        }
        Frame(); Frame(); io.AddMousePosEvent(hit.X, hit.Y); Frame();
        io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame();
        Check(value && changed == 1, "Clicking the last line of a wrapped checkbox did not toggle exactly once.");
        width = 760; Frame(); Check(value && changed == 1, "Resizing a wrapped checkbox changed its value.");
        io.AddMousePosEvent(-100, -100);
        Console.WriteLine("PASS wrapped checkbox: native multiline click, stable ID and value across resize.");
    }
}
