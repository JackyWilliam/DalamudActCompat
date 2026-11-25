using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Newtonsoft.Json;

// Meter text colour and outline are per-template options that default to the look every saved
// layout already has: an update must change nothing until the player turns one on.
internal static class MeterTextSmokeTests
{
    private static readonly Vector3 Picked = new(1, .9f, .2f);

    internal static void Run()
    {
        var profile = new MeterWindowProfile();
        Check(profile.TextColor is null && !profile.TextOutline, "A new meter template starts with a picked colour or an outline.");
        Check(MeterBackground.Text(profile) == Vector4.One, "The automatic colour on the default dark panel is no longer white.");
        var bright = new MeterWindowProfile { BackgroundColor = new(.95f, .93f, .88f), BackgroundOpacity = 1 };
        Check(MeterBackground.Text(bright) == new Vector4(.12f, .10f, .08f, 1), "A bright opaque panel no longer gets dark text.");
        bright.BackgroundOpacity = .3f;
        Check(MeterBackground.Text(bright) == Vector4.One, "A mostly transparent bright panel no longer gets white text.");
        bright.BackgroundOpacity = 1;
        bright.TextColor = Picked;
        Check(MeterBackground.Text(bright) == new Vector4(Picked, 1), "A picked colour did not replace the automatic one.");

        profile.TextColor = new Vector3(float.NaN, 4, -1);
        profile.Normalize(MeterSlotDefaults.CreateClassic());
        Check(profile.TextColor == new Vector3(1, 1, 0), "An invalid text colour reached rendering.");

        // Configurations saved before the options existed load as "automatic, no outline".
        var config = JsonConvert.DeserializeObject<PluginConfiguration>("""
            {"Version":17,"Meter":{"ClassicWindow":{"BackgroundOpacity":0},"HorizontalWindow":{"BackgroundOpacity":0}}}
            """)!;
        config.ApplyMigrations();
        var meter = config.Meter;
        foreach (var saved in new[] { meter.ClassicWindow, meter.HorizontalWindow, meter.RoleSplitWindow, meter.RoleSplitDamageWindow, meter.RoleSplitHealerWindow })
            Check(saved.TextColor is null && !saved.TextOutline, "An old configuration gained a text colour or an outline on load.");

        meter.HorizontalWindow.TextColor = Picked;
        meter.HorizontalWindow.TextOutline = true;
        meter.RoleSplitHealerWindow.TextOutline = true;
        var restored = JsonConvert.DeserializeObject<PluginConfiguration>(JsonConvert.SerializeObject(config))!;
        restored.ApplyMigrations();
        var snapshot = new PluginConfiguration();
        snapshot.RestoreFrom(restored.CreateSnapshot());
        Check(snapshot.Meter.HorizontalWindow.TextColor == Picked && snapshot.Meter.HorizontalWindow.TextOutline &&
              snapshot.Meter.RoleSplitHealerWindow.TextOutline && snapshot.Meter.RoleSplitHealerWindow.TextColor is null &&
              snapshot.Meter.ClassicWindow.TextColor is null && !snapshot.Meter.ClassicWindow.TextOutline,
            "Reload or snapshot lost a template's text options, or spread them to another template.");

        // The role-split panes and the legacy shared profile copy appearance in both directions.
        var roleSplit = new MeterSettings();
        roleSplit.RoleSplitDamageWindow.TextColor = Picked;
        roleSplit.RoleSplitDamageWindow.TextOutline = true;
        roleSplit.SynchronizeLegacyRoleSplitWindow();
        Check(roleSplit.RoleSplitWindow.TextColor == Picked && roleSplit.RoleSplitWindow.TextOutline &&
              roleSplit.RoleSplitHealerWindow.TextColor is null && !roleSplit.RoleSplitHealerWindow.TextOutline,
            "The legacy role-split profile did not mirror the D/T text options, or the healer pane changed with it.");
        roleSplit.MigrateIndependentRoleSplitWindows();
        Check(roleSplit.RoleSplitHealerWindow.TextColor == Picked && roleSplit.RoleSplitHealerWindow.TextOutline,
            "Splitting the shared role-split profile dropped its text options.");
        Console.WriteLine("Meter text: automatic colour unchanged, picked colour, invalid values, old configurations, reload/snapshot and role-split copies passed.");
    }

    // Draws the real windows: the options must reach every label, add nothing while they are
    // off, and not carry from one meter window into the next one drawn in the frame.
    internal static unsafe void RunNative()
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") ?? throw new InvalidOperationException("Native ImGui is required.");
        var nativePath = Path.Combine(AppContext.BaseDirectory, "cimgui.dll");
        try { File.Copy(library, nativePath, true); } catch (IOException) when (File.Exists(nativePath)) { }
        NativeLibrary.Load(nativePath);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0x024f, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            Check(io.Fonts.Build(), "Meter text font atlas failed.");
            io.FontGlobalScale = 1; io.DisplaySize = new(1000, 600);
            io.AddMouseButtonEvent(0, false); io.AddMousePosEvent(-100, -100);
            DactTheme.SetCurrent(new(), false, 0);
            var raster = new NativeUiRasterizer(io.Fonts);
            var output = Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT");
            if (output is not null) Directory.CreateDirectory(output);

            var config = new PluginConfiguration(); config.ApplyMigrations(); config.Fflogs.Enabled = false;
            var text = new UiText(config); var store = new EncounterStateStore();
            var encounter = (Encounter)typeof(MeterStyleEditorWindow).GetMethod("CreatePreviewEncounter", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [text])!;
            store.UpdateCurrent(encounter with { EndTime = encounter.StartTime.AddMinutes(3) });
            var service = new MeterService(store, config.Meter);
            var icons = new JobIconTextureSet(null!, Path.Combine(Path.GetTempPath(), "dact-missing-meter-text-icons"));
            using var logo = new SkinSmokeTests.EmptyTexture();
            var classic = new MeterWindow(service, null!, config, text, icons, logo, logo, logo, (_, name) => name, () => { });
            var horizontal = new HorizontalMeterWindow(service, config, text, icons, () => { });
            var damage = new RoleSplitMeterWindow(service, config, text, classic, () => { }, RoleSplitGroup.DamageTank);
            var healer = new RoleSplitMeterWindow(service, config, text, classic, () => { }, RoleSplitGroup.Healer);
            config.Meter.HorizontalWindow.IsEnabled = true;
            (Window Window, MeterWindowProfile Profile, string Name)[] windows =
            [
                (classic, config.Meter.ClassicWindow, "classic"),
                (horizontal, config.Meter.HorizontalWindow, "horizontal"),
                (damage, config.Meter.RoleSplitDamageWindow, "damage-tank"),
            ];
            var pickedRgb = ImGui.ColorConvertFloat4ToU32(new Vector4(Picked, 1)) & 0xffffff;

            void Render(Window window)
            {
                for (var frame = 0; frame < 3; frame++)
                {
                    ImGui.NewFrame();
                    // Half bright, half dark behind a transparent meter: no single text colour
                    // is readable over both, which is the case the outline is for.
                    var back = ImGui.GetBackgroundDrawList();
                    back.AddRectFilled(Vector2.Zero, new(io.DisplaySize.X / 2, io.DisplaySize.Y), 0xFFF4F0EA);
                    back.AddRectFilled(new(io.DisplaySize.X / 2, 0), io.DisplaySize, 0xFF22160E);
                    window.PreDraw();
                    ImGui.SetNextWindowPos(new(24, 24)); ImGui.SetNextWindowSize(new(950, 545));
                    ImGui.Begin(window.WindowName, window.Flags | ImGuiWindowFlags.NoSavedSettings);
                    window.Draw(); ImGui.End(); window.PostDraw(); ImGui.Render();
                }
            }

            // Vertices in total, vertices of an outline edge, vertices in the picked colour. An edge
            // is translucent black behind bright text and translucent white behind dark text.
            (int Total, int Edge, int Picked) Count()
            {
                var total = 0; var edge = 0; var picked = 0; var draw = ImGui.GetDrawData();
                for (var n = 0; n < draw.CmdListsCount; n++)
                {
                    var list = new ImDrawListPtr(draw.CmdLists[n]);
                    for (var v = 0; v < list.VtxBuffer.Size; v++)
                    {
                        var color = list.VtxBuffer[v].Col;
                        total++;
                        if ((color & 0xffffff) is 0 or 0xffffff && (color >> 24) is > 0 and < 255) edge++;
                        if ((color & 0xffffff) == pickedRgb) picked++;
                    }
                }
                return (total, edge, picked);
            }

            void Save(string name)
            {
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, name + ".png"));
            }

            foreach (var (window, profile, name) in windows)
            {
                profile.IsLocked = true; profile.ClickThroughWhenLocked = false;
                profile.BackgroundOpacity = 0;
                Render(window);
                var plain = Count();
                Check(plain.Picked == 0, $"{name}: the picked colour appears before any colour was picked.");
                Save($"meter-text-{name}-plain");

                profile.TextOutline = true;
                Render(window);
                var outlined = Count();
                Check(outlined.Edge > plain.Edge, $"{name}: the outline added no edge behind the text.");
                Check(outlined.Total - plain.Total == outlined.Edge - plain.Edge,
                    $"{name}: the outline changed something other than adding its edge ({outlined.Total - plain.Total} vertices, {outlined.Edge - plain.Edge} of them edge).");
                Save($"meter-text-{name}-outline");

                // Another meter window drawn right after must not inherit the outline.
                var other = windows.First(item => !ReferenceEquals(item.Profile, profile));
                other.Profile.IsLocked = true; other.Profile.TextOutline = false;
                Render(other.Window);
                var otherPlain = Count();
                Render(window);
                Render(other.Window);
                Check(Count() == otherPlain, $"{name}: its outline carried into the {other.Name} window.");

                profile.TextColor = Picked;
                Render(window);
                var coloured = Count();
                Check(coloured.Picked > 0, $"{name}: no text took the picked colour.");
                Check(coloured.Total == outlined.Total, $"{name}: picking a colour changed how much is drawn.");
                Save($"meter-text-{name}-outline-picked");

                profile.TextOutline = false; profile.TextColor = null;
                Render(window);
                Check(Count() == plain, $"{name}: turning both options off did not restore the original drawing.");
            }

            // The style editor: both controls sit at the end of the appearance column, and the
            // preview follows them while editing.
            // The editor opens on the template in use; with Horizontal off again that is Classic.
            config.Meter.HorizontalWindow.IsEnabled = false;
            config.Meter.ClassicWindow.TextOutline = true; config.Meter.ClassicWindow.TextColor = Picked;
            var editor = new MeterStyleEditorWindow(config, logo, classic, horizontal, damage, healer, text, () => { }); editor.Open();
            io.DisplaySize = new(1200, 1500);
            foreach (var language in UiLanguages.All)
            {
                config.UiLanguage = language.Id;
                for (var frame = 0; frame < 4; frame++)
                {
                    ImGui.NewFrame();
                    using (DactTheme.PushFrame())
                    {
                        editor.PreDraw(); ImGui.SetNextWindowPos(new(25, 25)); ImGui.SetNextWindowSize(new(1140, 1430));
                        ImGui.Begin("meter-text-editor", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDecoration);
                        editor.Draw(); ImGui.End(); editor.PostDraw();
                    }
                    ImGui.Render();
                }
                Check(Count().Picked > 0, $"{language.Id}: the editor preview ignored the picked text colour.");
                Save($"meter-text-editor-{language.Id}");
            }
            Console.WriteLine("Meter text native: Classic, Horizontal and D/T draw the outline and the picked colour on every label, add nothing when off, keep them to their own window, and the six-language editor rendered.");
        }
        finally
        {
            DactTheme.SetCurrent(new(), false, 0);
            ImGui.DestroyContext(context);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
