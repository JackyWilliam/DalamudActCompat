using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Newtonsoft.Json;

internal static partial class SkinSmokeTests
{
    private static void MeterOpacityConfiguration()
    {
        var config = JsonConvert.DeserializeObject<PluginConfiguration>("""
            {"Version":17,"Meter":{"ClassicWindow":{"BackgroundOpacity":0.42},
            "RoleSplitDamageWindow":{"BackgroundOpacity":0},"RoleSplitHealerWindow":{"BackgroundOpacity":1}}}
            """)!;
        config.ApplyMigrations();
        var meter = config.Meter;
        Check(Math.Abs(meter.ClassicWindow.DataBarOpacity!.Value - .084f) < .00001f &&
            meter.RoleSplitDamageWindow.DataBarOpacity == 0 && meter.RoleSplitHealerWindow.DataBarOpacity == .2f,
            "Old profiles did not capture their own bar opacity.");
        foreach (var oldAlpha in new[] { .17f, .2f, .32f, .45f, .55f })
            Check(Math.Abs(MeterBackground.Bar(meter.ClassicWindow, new(.3f, .5f, .7f, oldAlpha)).W - oldAlpha * .42f) < .00001f,
                "Migration changed the existing job/local-player/compact shading.");
        // v13's single shared opacity must migrate before the new bar value is captured.
        var older = JsonConvert.DeserializeObject<PluginConfiguration>("""{"Version":13,"Meter":{"BackgroundOpacity":0.36}}""")!;
        older.ApplyMigrations();
        Check(Math.Abs(older.Meter.ClassicWindow.DataBarOpacity!.Value - .072f) < .00001f &&
            Math.Abs(older.Meter.RoleSplitHealerWindow.DataBarOpacity!.Value - .072f) < .00001f,
            "Bar migration ran before legacy background/role-pane migration.");
        meter.ClassicWindow.BackgroundOpacity = 0;
        meter.ClassicWindow.DataBarOpacity = .6f;
        meter.RoleSplitDamageWindow.DataBarOpacity = 0;
        meter.RoleSplitHealerWindow.DataBarOpacity = 1;
        config.ApplyMigrations();
        var restored = JsonConvert.DeserializeObject<PluginConfiguration>(JsonConvert.SerializeObject(config))!;
        restored.ApplyMigrations();
        var snapshot = new PluginConfiguration(); snapshot.RestoreFrom(restored.CreateSnapshot()); snapshot.ApplyMigrations();
        Check(snapshot.Version == 17 && snapshot.Meter.ClassicWindow.BackgroundOpacity == 0 &&
            snapshot.Meter.ClassicWindow.DataBarOpacity == .6f && snapshot.Meter.RoleSplitDamageWindow.DataBarOpacity == 0 &&
            snapshot.Meter.RoleSplitHealerWindow.DataBarOpacity == 1,
            "Reload/snapshot changed independent opacity or the configuration version.");
        Check(!snapshot.ApplyMigrations(), "Repeat migration modified already captured opacity.");
        var profile = snapshot.Meter.ClassicWindow;
        Check(MeterBackground.Fill(profile).W == 0 && Math.Abs(MeterBackground.Bar(profile, new(.3f, .5f, .7f, .2f)).W - .6f) < .00001f,
            "Transparent panel still suppresses data bars.");
        profile.BackgroundOpacity = 1; profile.DataBarOpacity = 0;
        Check(MeterBackground.Fill(profile).W == 1 && MeterBackground.Bar(profile, new(.3f, .5f, .7f, .2f)).W == 0,
            "Hidden bars also hide the panel.");
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, -3f, 4f })
        {
            profile.DataBarOpacity = invalid; profile.Normalize(MeterSlotDefaults.CreateClassic());
            Check(profile.DataBarOpacity is >= 0 and <= 1, "Invalid bar alpha reached rendering.");
        }
        Console.WriteLine("Meter opacity: old configuration, per-pane migration, local accent, zero/one, reload/snapshot and invalid values passed.");
    }

    private static unsafe void MeterOpacityNative(NativeUiRasterizer raster, string? output, EmptyTexture logo)
    {
        var config = new PluginConfiguration(); config.ApplyMigrations(); config.Fflogs.Enabled = false;
        var store = new EncounterStateStore();
        var encounter = (Encounter)typeof(MeterStyleEditorWindow).GetMethod("CreatePreviewEncounter", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [new UiText(config)])!;
        store.UpdateCurrent(encounter with { EndTime = encounter.StartTime.AddMinutes(3) });
        var service = new MeterService(store, config.Meter); var text = new UiText(config);
        var icons = new JobIconTextureSet(null!, Path.Combine(Path.GetTempPath(), "dact-missing-preview-icons"));
        var classic = new MeterWindow(service, null!, config, text, icons, logo, logo, logo, (_, name) => name, () => { });
        var horizontal = new HorizontalMeterWindow(service, config, text, icons, () => { });
        var dt = new RoleSplitMeterWindow(service, config, text, classic, () => { }, RoleSplitGroup.DamageTank);
        var healer = new RoleSplitMeterWindow(service, config, text, classic, () => { }, RoleSplitGroup.Healer);
        (Window Window, MeterWindowProfile Profile, string Name, bool Alliance)[] windows =
        [
            (classic, config.Meter.ClassicWindow, "classic", false),
            (classic, config.Meter.ClassicWindow, "alliance", true),
            (dt, config.Meter.RoleSplitDamageWindow, "damage-tank", false),
            (healer, config.Meter.RoleSplitHealerWindow, "healer", false),
        ];
        var jobRgb = new[] { new Vector4(.28f, .52f, .84f, 1), new(.32f, .70f, .48f, 1), new(.82f, .34f, .34f, 1), new(.76f, .58f, .28f, 1), new(.60f, .42f, .78f, 1) }
            .Select(color => ImGui.ColorConvertFloat4ToU32(color) & 0xffffff).ToHashSet();
        var localRgb = ImGui.ColorConvertFloat4ToU32(config.Meter.LocalPlayerColor) & 0xffffff;
        jobRgb.Add(localRgb);
        var io = ImGui.GetIO(); io.FontGlobalScale = 1; io.DisplaySize = new(1000, 600); io.AddMousePosEvent(-100, -100);
        DactTheme.SetCurrent(new(), false, 0);
        foreach (var (window, profile, name, alliance) in windows)
        {
            config.Meter.ClassicAllianceView = alliance;
            profile.IsLocked = true; profile.ClickThroughWhenLocked = false;
            string? baselineBars = null;
            foreach (var panelAlpha in new[] { 0f, .4f, 1f })
            {
                profile.BackgroundOpacity = panelAlpha; profile.DataBarOpacity = .6f;
                Render();
                var bars = Colors(true);
                Check(bars.Length > 0, $"{name}: no visible job bars with panel opacity {panelAlpha}.");
                baselineBars ??= bars;
                Check(bars == baselineBars, $"{name}: panel opacity changed real bar vertices.");
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"meter-opacity-{name}-panel{panelAlpha * 100:0}-bars60.png"));
                var textBefore = Colors(false);
                profile.DataBarOpacity = 0; Render();
                Check(Colors(true).Length == 0, $"{name}: zero data-bar opacity still emits colored bars.");
                Check(Colors(false) == textBefore, $"{name}: data-bar opacity changed opaque text/icon colors.");
            }
            void Render()
            {
                for (var frame = 0; frame < 3; frame++)
                {
                    ImGui.NewFrame(); window.PreDraw();
                    ImGui.SetNextWindowPos(new(24, 24)); ImGui.SetNextWindowSize(new(950, 545));
                    ImGui.Begin(window.WindowName, window.Flags | ImGuiWindowFlags.NoSavedSettings);
                    window.Draw(); ImGui.End(); window.PostDraw(); ImGui.Render();
                }
            }
            string Colors(bool bars)
            {
                var colors = new List<uint>(); var draw = ImGui.GetDrawData();
                for (var n = 0; n < draw.CmdListsCount; n++)
                {
                    var list = new ImDrawListPtr(draw.CmdLists[n]);
                    for (var v = 0; v < list.VtxBuffer.Size; v++)
                    {
                        var color = list.VtxBuffer[v].Col;
                        // The existing local-player outline stays visible at zero
                        // bar opacity; it uses the same RGB with alpha .95.
                        if (bars ? jobRgb.Contains(color & 0xffffff) && ((color & 0xffffff) != localRgb || (color >> 24) == 255)
                            : (color >> 24) == 255 && !jobRgb.Contains(color & 0xffffff)) colors.Add(color);
                    }
                }
                return string.Join(',', colors);
            }
        }
        config.Meter.ClassicAllianceView = false;
        config.Meter.ClassicWindow.BackgroundOpacity = 0; config.Meter.ClassicWindow.DataBarOpacity = .6f;
        var saves = 0;
        var editor = new MeterStyleEditorWindow(config, logo, classic, horizontal, dt, healer, text, () => saves++); editor.Open();
        io.DisplaySize = new(1200, 900);
        void EditorFrame()
        {
            ImGui.NewFrame(); using (DactTheme.PushFrame())
            {
                editor.PreDraw(); ImGui.SetNextWindowPos(new(25, 25)); ImGui.SetNextWindowSize(new(1140, 830));
                ImGui.Begin("meter-opacity-editor", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDecoration);
                editor.Draw(); ImGui.End(); editor.PostDraw();
            }
            ImGui.Render();
        }
        foreach (var language in UiLanguages.All)
        {
            config.UiLanguage = language.Id;
            for (var frame = 0; frame < 3; frame++) EditorFrame();
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"meter-opacity-editor-{language.Id}.png"));
        }
        config.UiLanguage = "zh-CN"; EditorFrame(); EditorFrame();
        void Click(Vector2 position)
        {
            io.AddMousePosEvent(position.X, position.Y); EditorFrame();
            io.AddMouseButtonEvent(0, true); EditorFrame(); io.AddMouseButtonEvent(0, false); EditorFrame(); EditorFrame();
        }
        void SelectCollapseDirection(MeterCollapseDirection direction)
        {
            Click(new(1020, 545));
            var comboContext = ImGui.GetCurrentContext();
            Check(comboContext.OpenPopupStack.Size == 1, "Collapse direction menu did not open.");
            var combo = new ImGuiWindowPtr(comboContext.OpenPopupStack[0].Window);
            Click(combo.Pos + combo.WindowPadding + new Vector2(40, ((int)direction + .5f) * ImGui.GetTextLineHeightWithSpacing()));
            Check(config.Meter.ClassicWindow.CollapseDirection == direction && comboContext.OpenPopupStack.Size == 0,
                "The actual collapse direction selector did not update the profile.");
        }
        // Hit the actual editor controls at the fixed native preview dimensions.
        // These clicks are queued only to this isolated cimgui context.
        Click(new(1020, 407));
        var panelValue = config.Meter.ClassicWindow.BackgroundOpacity;
        Check(panelValue is > .4f and < .6f && config.Meter.ClassicWindow.DataBarOpacity == .6f, "Panel slider changed the bars or did not respond.");
        Click(new(1070, 461));
        var barValue = config.Meter.ClassicWindow.DataBarOpacity;
        Check(barValue is > .65f and < .8f && config.Meter.ClassicWindow.BackgroundOpacity == panelValue, "Bar slider changed the panel or did not respond.");
        Click(new(1022, 356));
        Check(config.Meter.ClassicWindow.BackgroundOpacity == 0 && config.Meter.ClassicWindow.DataBarOpacity == barValue, "Transparent panel button hid the bars.");
        SelectCollapseDirection(MeterCollapseDirection.Downward);
        Click(new(980, 835));
        Check(saves == 1 && config.Meter.ClassicWindow.DataBarOpacity == barValue, "Editor save lost the independent bar opacity.");
        Check(config.Meter.ClassicWindow.CollapseDirection == MeterCollapseDirection.Downward &&
            config.Meter.RoleSplitDamageWindow.CollapseDirection == MeterCollapseDirection.Upward &&
            config.Meter.RoleSplitHealerWindow.CollapseDirection == MeterCollapseDirection.Upward,
            "Editor Save lost the collapse direction or changed another profile.");
        editor.Open(); EditorFrame(); EditorFrame(); Click(new(935, 461));
        Check(config.Meter.ClassicWindow.DataBarOpacity != barValue, "Cancel fixture did not edit the bar value.");
        SelectCollapseDirection(MeterCollapseDirection.Upward);
        Click(new(1100, 835));
        var context = ImGui.GetCurrentContext();
        Check(context.OpenPopupStack.Size == 1, "Cancel bypassed the existing unsaved-changes confirmation.");
        var popup = new ImGuiWindowPtr(context.OpenPopupStack[0].Window);
        Click(popup.Pos + new Vector2(popup.Size.X / 2, popup.Size.Y - popup.WindowPadding.Y - DactTheme.ButtonHeight / 2));
        Check(config.Meter.ClassicWindow.DataBarOpacity == barValue && config.Meter.ClassicWindow.BackgroundOpacity == 0 && saves == 2,
            "Exit without saving failed to restore and persist the original values.");
        Check(config.Meter.ClassicWindow.CollapseDirection == MeterCollapseDirection.Downward,
            "Cancel failed to restore the saved collapse direction.");
        Console.WriteLine("Meter opacity native: Classic 8/24, D/T and H real bar vertices independent of panel, zero bars, text preserved and six-language editor rendered.");
        Console.WriteLine("Meter opacity editor: both actual sliders, transparent-panel button, Save and Cancel passed.");
        Console.WriteLine("Meter collapse editor: actual direction selector, independent profile, Save and Cancel passed.");
    }
}
